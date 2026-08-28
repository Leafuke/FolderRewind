using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

public sealed class HistoryMetadataSyncService
{
    private readonly HistoryRuntime _history;
    private readonly IHistoryMetadataTransport _transport;
    private readonly IHistoryLegacyCloudBridge? _legacyBridge;
    private readonly HistoryPackCodec _codec = new();

    public HistoryMetadataSyncService(
        HistoryRuntime history,
        IHistoryMetadataTransport transport,
        IHistoryLegacyCloudBridge? legacyBridge = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _legacyBridge = legacyBridge;
    }

    public async Task<HistoryMetadataSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        int downloaded = 0;
        int uploaded = 0;
        try
        {
            var descriptor = await _transport.ReadDescriptorAsync(_history.ConfigId, cancellationToken)
                .ConfigureAwait(false);
            if (descriptor is null)
            {
                var localPacks = await _history.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
                if (localPacks.Count == 0
                    && _legacyBridge is not null
                    && await _transport.LegacyHistoryExistsAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false))
                {
                    _ = await _legacyBridge.TryBridgeAsync(cancellationToken).ConfigureAwait(false);
                }
                var canonical = HistoryRepositoryDescriptor.Create(_history.ConfigId).ToCanonicalBytes();
                await _transport.CreateDescriptorOnceAsync(_history.ConfigId, canonical, cancellationToken)
                    .ConfigureAwait(false);
                descriptor = await _transport.ReadDescriptorAsync(_history.ConfigId, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (descriptor is null)
                return Result(HistoryMetadataSyncStatus.Failed, downloaded, uploaded, "Remote descriptor creation was not observable.");
            var remoteDescriptor = HistoryRepositoryDescriptor.Parse(descriptor);
            if (remoteDescriptor.ConfigId != _history.ConfigId
                || !descriptor.AsSpan().SequenceEqual(HistoryRepositoryDescriptor.Create(_history.ConfigId).ToCanonicalBytes()))
            {
                return Result(HistoryMetadataSyncStatus.IntegrityConflict, downloaded, uploaded, "Remote descriptor identity or canonical bytes differ.");
            }

            var firstPull = await PullRemoteOnlyAsync(cancellationToken).ConfigureAwait(false);
            downloaded += firstPull.Downloaded;
            if (firstPull.Status == HistoryMetadataSyncStatus.RemoteRepositoryIncomplete)
            {
                var retryPull = await PullRemoteOnlyAsync(cancellationToken).ConfigureAwait(false);
                downloaded += retryPull.Downloaded;
                if (retryPull.Status != HistoryMetadataSyncStatus.Succeeded)
                    return Result(retryPull.Status, downloaded, uploaded, retryPull.Diagnostic);
            }
            else if (firstPull.Status != HistoryMetadataSyncStatus.Succeeded)
            {
                return Result(firstPull.Status, downloaded, uploaded, firstPull.Diagnostic);
            }

            var remoteIds = (await _transport.ListPacksAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false)).ToHashSet();
            foreach (var local in await _history.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false))
            {
                if (remoteIds.Contains(local.Pack.PackId)) continue;
                await _transport.UploadPackOnceAsync(
                    _history.ConfigId,
                    local.Pack.PackId,
                    local.OriginalBytes,
                    cancellationToken).ConfigureAwait(false);
                uploaded++;
            }

            var secondPull = await PullRemoteOnlyAsync(cancellationToken).ConfigureAwait(false);
            downloaded += secondPull.Downloaded;
            if (secondPull.Status != HistoryMetadataSyncStatus.Succeeded)
                return Result(secondPull.Status, downloaded, uploaded, secondPull.Diagnostic);

            await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
            var branches = await _history.Query.GetBranchesAsync(cancellationToken).ConfigureAwait(false);
            var divergent = branches.Branches.Where(item => item.IsMultiTip)
                .Select(item => item.BranchId).ToImmutableArray();
            var collisions = branches.BranchNameCollisions.Select(item => item.Name).ToImmutableArray();
            _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.CloudUnionChanged);
            return new(
                HistoryMetadataSyncStatus.Succeeded,
                downloaded,
                uploaded,
                divergent,
                collisions,
                divergent.Length == 0 && collisions.Length == 0
                    ? string.Empty
                    : "Metadata union succeeded with Branch divergence or name collisions.");
        }
        catch (HistoryPackCompatibilityException ex)
        {
            return Result(HistoryMetadataSyncStatus.CompatibilityBlocked, downloaded, uploaded, ex.Message);
        }
        catch (HistoryIntegrityConflictException ex)
        {
            return Result(HistoryMetadataSyncStatus.IntegrityConflict, downloaded, uploaded, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result(HistoryMetadataSyncStatus.Failed, downloaded, uploaded, ex.Message);
        }
    }

    private async Task<PullResult> PullRemoteOnlyAsync(CancellationToken cancellationToken)
    {
        var local = (await _history.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false))
            .Select(item => item.Pack.PackId).ToHashSet();
        var remote = await _transport.ListPacksAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false);
        var incoming = new List<byte[]>();
        var decodedIncoming = new List<HistoryPackReadResult>();
        foreach (var packId in remote.Where(id => !local.Contains(id)).OrderBy(id => id.ToString(), StringComparer.Ordinal))
        {
            var bytes = await _transport.DownloadPackAsync(_history.ConfigId, packId, cancellationToken).ConfigureAwait(false);
            HistoryPackReadResult decoded;
            try
            {
                decoded = _codec.Decode(bytes);
            }
            catch (HistoryPackCompatibilityException)
            {
                throw;
            }
            catch (HistoryRepositoryException ex)
            {
                try
                {
                    await _history.Repository.ImportAsync(
                        [(ReadOnlyMemory<byte>)bytes],
                        cancellationToken).ConfigureAwait(false);
                }
                catch { }
                throw new HistoryIntegrityConflictException("Remote Pack envelope is invalid: " + ex.Message);
            }
            if (decoded.Pack.PackId != packId)
                throw new HistoryIntegrityConflictException("Remote Pack path identity differs from its envelope.");
            incoming.Add(bytes);
            decodedIncoming.Add(decoded);
        }
        if (incoming.Count == 0) return new(HistoryMetadataSyncStatus.Succeeded, 0, string.Empty);

        try
        {
            await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var existing = await _history.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                new HistoryRepositoryValidator(_codec).Validate(
                    _history.ConfigId,
                    existing.Concat(decodedIncoming));
            }
            catch (HistoryIntegrityConflictException)
            {
                // Let the repository perform its create-once conflict handling and quarantine
                // the complete incoming batch rather than selecting individual objects.
                await _history.Repository.ImportAsync(
                    incoming.Select(bytes => (ReadOnlyMemory<byte>)bytes),
                    cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (HistoryRepositoryValidationException ex)
            {
                return new(
                    HistoryMetadataSyncStatus.RemoteRepositoryIncomplete,
                    0,
                    "RemoteRepositoryIncomplete/RepairRequired: " + ex.Message);
            }
            await _history.Repository.ImportAsync(
                incoming.Select(bytes => (ReadOnlyMemory<byte>)bytes),
                cancellationToken).ConfigureAwait(false);
            await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
            return new(HistoryMetadataSyncStatus.Succeeded, incoming.Count, string.Empty);
        }
        catch (HistoryRepositoryValidationException ex)
        {
            return new(HistoryMetadataSyncStatus.RemoteRepositoryIncomplete, 0,
                "RemoteRepositoryIncomplete/RepairRequired: " + ex.Message);
        }
    }

    private static HistoryMetadataSyncResult Result(
        HistoryMetadataSyncStatus status,
        int downloaded,
        int uploaded,
        string diagnostic)
        => new(status, downloaded, uploaded, [], [], diagnostic);

    private sealed record PullResult(HistoryMetadataSyncStatus Status, int Downloaded, string Diagnostic);
}

public sealed class HistoryCloudSyncCoordinator
{
    private readonly Func<CancellationToken, Task<HistoryMetadataSyncResult>> _sync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pending;

    public HistoryCloudSyncCoordinator(Func<CancellationToken, Task<HistoryMetadataSyncResult>> sync)
        => _sync = sync ?? throw new ArgumentNullException(nameof(sync));

    public async Task<HistoryMetadataSyncResult> RequestSyncAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _pending, 1);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HistoryMetadataSyncResult? result = null;
            do
            {
                Interlocked.Exchange(ref _pending, 0);
                result = await _sync(cancellationToken).ConfigureAwait(false);
            }
            while (Interlocked.CompareExchange(ref _pending, 0, 0) != 0);
            return result!;
        }
        finally
        {
            _gate.Release();
        }
    }
}
