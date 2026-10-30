using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.LocalState;

internal sealed record HistoryWorkspaceRepairPreview(HistoryConfigId ConfigId, ImmutableArray<SourceId> Sources,
    string ConfigurationSignature, DeviceLocalStateStatus Status, bool UnsupportedFormat, string? Sha256,
    RestoreObjectIdentity? Identity, RestoreObjectIdentity? ParentIdentity, string PreservationDirectory)
{
    public bool CanRepair => !UnsupportedFormat && Status is DeviceLocalStateStatus.Missing or DeviceLocalStateStatus.Corrupt;
}
internal sealed record HistoryWorkspaceRepairResult(bool Rebuilt, HistoryWorkspace Workspace, string? PreservedPath);

public sealed partial class HistoryWorkspaceStore
{
    internal Task<HistoryWorkspaceRepairPreview> PreviewRepairAsync(IReadOnlyCollection<SourceId> sources, string signature, CancellationToken token)
        => WithGateAsync(() => Task.FromResult(PreviewInsideGate(sources, signature)), token);

    private HistoryWorkspaceRepairPreview PreviewInsideGate(IReadOnlyCollection<SourceId> sources, string signature)
    {
        if (sources.Distinct().Count() != sources.Count) throw new InvalidDataException("Repair Source roster is ambiguous.");
        var state = LoadInsideGate();
        string? hash = null;
        RestoreObjectIdentity? identity = null;
        if (state.Status == DeviceLocalStateStatus.Corrupt && !state.IsUnsupportedFormat)
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            identity = WindowsRestoreFileSystem.Identity(stream.SafeFileHandle);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        return new(_configId, [.. sources.OrderBy(s => s.ToString(), StringComparer.Ordinal)], signature, state.Status,
            state.IsUnsupportedFormat, hash, identity,
            state.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Missing && !state.IsUnsupportedFormat
                ? WindowsRestoreFileSystem.Identity(Path.GetDirectoryName(_path)!) : null, Path.GetDirectoryName(_path)!);
    }

    internal Task<HistoryWorkspaceRepairResult> RepairAsync(HistoryWorkspaceRepairPreview preview, CancellationToken token)
        => WithGateAsync(async () =>
        {
            if (preview.ConfigId != _configId) throw new DeviceLocalStateConflictException("Repair configuration differs from Store.");
            var loaded = LoadInsideGate();
            if (loaded.Status == DeviceLocalStateStatus.Valid)
                return new HistoryWorkspaceRepairResult(false, loaded.Value!, null);
            var current = PreviewInsideGate(preview.Sources, preview.ConfigurationSignature);
            if (preview.ConfigId != _configId || !preview.CanRepair || !current.CanRepair || current.ParentIdentity != preview.ParentIdentity || current.Status != preview.Status || current.Sha256 != preview.Sha256 || current.Identity != preview.Identity)
                throw new DeviceLocalStateConflictException("Workspace changed after repair preview; inspect it again.");
            using var parents = new WindowsDirectoryReadLease(_path);
            if (WindowsRestoreFileSystem.Identity(Path.GetDirectoryName(_path)!) != preview.ParentIdentity)
                throw new DeviceLocalStateConflictException("Workspace parent changed before repair.");
            var rebuilt = new HistoryWorkspace(_configId, 0, preview.Sources.Select(source =>
                new WorkspaceSourceBaseline(source, null, WorkspaceBaselineRelation.Unknown, RequiresFullCapture: true)
                { ContentRealizationId = Guid.NewGuid() }));
            var ready = _path + "." + Guid.NewGuid().ToString("N") + ".repair-ready";
            string? backup = null;
            string? retired = null;
            try
            {
                await AtomicFileService.WriteAsync(ready, (stream, ct) => JsonSerializer.SerializeAsync(stream, rebuilt, JsonOptions, ct), token).ConfigureAwait(false);
                if (preview.Status == DeviceLocalStateStatus.Corrupt)
                {
                    var bytes = await File.ReadAllBytesAsync(_path, token).ConfigureAwait(false);
                    if (Convert.ToHexString(SHA256.HashData(bytes)) != preview.Sha256) throw new DeviceLocalStateConflictException("Workspace bytes changed after preview.");
                    backup = _path + "." + Guid.NewGuid().ToString("N") + ".original";
                    using (var preserved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    { preserved.Write(bytes); preserved.Flush(true); }
                    ReachRepairPoint("after-preservation");
                    retired = backup + ".object";
                    // Verify bytes while holding the same object against writes/deletes, then retire it without replacement.
                    // Publishing below is create-only: an intervening foreign file is never overwritten.
                    WindowsRestoreFileSystem.MoveOwnedNoReplace(_path, retired, preview.Identity!, expectedContentHash: preview.Sha256);
                }
                token.ThrowIfCancellationRequested();
                ReachRepairPoint("before-publication");
                File.Move(ready, _path, overwrite: false);
                ReachRepairPoint("after-publication");
                return new HistoryWorkspaceRepairResult(true, rebuilt, backup);
            }
            catch
            {
                if (retired is not null && !Path.Exists(_path))
                {
                    try { WindowsRestoreFileSystem.MoveOwnedNoReplace(retired, _path, preview.Identity!); }
                    catch (IOException) { /* Preserved bytes and original object remain available. */ }
                }
                throw;
            }
            finally
            {
                try { if (File.Exists(ready)) File.Delete(ready); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A private incomplete candidate is disposable. */ }
            }
        }, token);

    private void ReachRepairPoint(string point)
    {
#if FOLDERREWIND_PROTOCOL_TESTS
        RepairFault?.Invoke(point);
#endif
    }
#if FOLDERREWIND_PROTOCOL_TESTS
    internal Action<string>? RepairFault { get; set; }
#endif
}

internal sealed class HistoryWorkspaceRepairService(HistoryRuntime history)
{
    private void RequireRecovered()
    {
        HistoryRestoreTransactionJournalStore.RequireRecovered(history.Repository.Paths.TransactionsRoot);
        history.Repository.Journals.RequireRecovered();
        history.Repository.Imports.RequireRecovered();
        history.MigrationState.RequireRecovered();
        Retention.HistoryChainRewriteJournalStore.RequireRecovered(history.Repository.Paths.LocalStateRoot);
    }

    internal async Task<HistoryWorkspaceRepairPreview> PreviewAsync(IReadOnlyCollection<SourceId> sources, string signature, CancellationToken token = default)
    {
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(history.ConfigId, token).ConfigureAwait(false);
        await using var mutation = await history.MutationGate.EnterForRecoveryAsync(token).ConfigureAwait(false);
        RequireRecovered();
        return await history.WorkspaceStore.PreviewRepairAsync(sources, signature, token).ConfigureAwait(false);
    }

    internal async Task<HistoryWorkspaceRepairResult> RepairAsync(HistoryWorkspaceRepairPreview preview, IReadOnlyCollection<SourceId> sources,
        string signature, CancellationToken token = default)
    {
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(history.ConfigId, token).ConfigureAwait(false);
        return await RepairInsideOperationAsync(preview, sources, signature, operation, token).ConfigureAwait(false);
    }

    internal async Task<HistoryWorkspaceRepairResult> RepairInsideOperationAsync(HistoryWorkspaceRepairPreview preview,
        IReadOnlyCollection<SourceId> sources, string signature, NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken token)
    {
        operation.Require(history.ConfigId);
        if (preview.ConfigId != history.ConfigId || signature != preview.ConfigurationSignature || !sources.OrderBy(s => s.ToString(), StringComparer.Ordinal).SequenceEqual(preview.Sources))
            throw new DeviceLocalStateConflictException("Configuration changed after repair preview.");
        await using var mutation = await history.MutationGate.EnterForRecoveryAsync(token).ConfigureAwait(false);
        RequireRecovered();
        Exception? failure = null;
        try
        {
            var result = await history.WorkspaceStore.RepairAsync(preview, token).ConfigureAwait(false);
            if (result.Rebuilt)
                foreach (var source in sources)
                {
                    var cache = await history.CaptureBaselines.LoadAsync(source, token).ConfigureAwait(false);
                    if (cache is not null) await history.CaptureBaselines.RemoveAsync(source, cache.Revision, token).ConfigureAwait(false);
                }
            return result;
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try { await history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false); }
            catch when (failure is not null) { }
            history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.LocalStateChanged);
        }
    }
}
