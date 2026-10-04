using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Services;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

public sealed record HistoryRewriteRetiredFile(string Path, long Size, string StorageSha256,
    ImmutableArray<RepresentationId> Representations);
public sealed record HistoryRewriteMapping(RepresentationId PreviousId, VersionRepresentation Replacement, string Path, int DeltaDepth,
    bool StorageRelocationOnly = false);
public sealed record HistoryRewriteFileWitness(string Path, long Size, string StorageSha256);
public sealed record HistoryChainRewriteJournal(HistoryTransactionId OperationId, PackId? PackId,
    bool StateApplied, bool Complete, ImmutableArray<HistoryRewriteRetiredFile> RetiredFiles,
    ImmutableArray<HistoryRewriteMapping> Mappings,
    ImmutableArray<HistoryRewriteFileWitness> VerifiedFiles = default,
    string? BackupRoot = null);

public sealed class HistoryChainRewriteJournalStore(HistoryRuntime history, Action<string>? checkpoint = null, string? backupRoot = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string Root => Path.Combine(history.Repository.Paths.LocalStateRoot, "chain-rewrites");
    public string? BackupRoot { get; } = backupRoot is null ? null : HistoryRewriteStoragePaths.NormalizeBackupRoot(backupRoot);
    public string WorkRoot(HistoryTransactionId id) => WorkRootAt(id, BackupRoot);
    public string PayloadRoot(HistoryTransactionId id) => PayloadRootAt(id, BackupRoot);
    private string WorkRootAt(HistoryTransactionId id, string? destination) => destination is null
        ? Path.Combine(history.Repository.Paths.TransactionsRoot, "rewrite-" + id)
        : Path.Combine(HistoryRewriteStoragePaths.OperationRoot(destination, history.ConfigId, id), "work");
    private string PayloadRootAt(HistoryTransactionId id, string? destination) => destination is null
        ? Path.Combine(history.Repository.Paths.RepositoryRoot, "payloads", "rewrite-" + id)
        : Path.Combine(HistoryRewriteStoragePaths.OperationRoot(destination, history.ConfigId, id), "payloads");
    private string PathFor(HistoryTransactionId id) => Path.Combine(Root, id + ".json");
    public void Save(HistoryChainRewriteJournal journal)
        => AtomicFileService.Write(PathFor(journal.OperationId), stream => JsonSerializer.Serialize(stream,
            journal with { VerifiedFiles = journal.VerifiedFiles.IsDefault ? [] : journal.VerifiedFiles }, Json));

    public static void RequireRecovered(string localStateRoot)
    {
        var root = Path.Combine(localStateRoot, "chain-rewrites");
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var journal = Read(path);
            if (journal.PackId is not null && !journal.StateApplied && !journal.Complete)
                throw new InvalidOperationException("An interrupted backup chain rewrite requires recovery.");
        }
    }

    internal static bool HasPending(string localStateRoot, HistoryTransactionId? exceptOperation = null)
    {
        var root = Path.Combine(localStateRoot, "chain-rewrites");
        return Directory.Exists(root) && Directory.EnumerateFiles(root, "*.json")
            .Select(Read).Any(journal => !journal.Complete && journal.OperationId != exceptOperation);
    }

    public async Task<bool> RecoverAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(Root)) return true;
        var complete = true;
        foreach (var path in Directory.EnumerateFiles(Root, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            var journal = Read(path);
            if (journal.Complete) continue;
            complete &= await FinishAsync(journal, token).ConfigureAwait(false);
        }
        return complete;
    }

    public async Task<bool> FinishAsync(HistoryChainRewriteJournal journal, CancellationToken token = default)
    {
        // Recovery uses the transaction's destination, not a possibly changed current configuration.
        if (journal.BackupRoot is not null
            && !Directory.Exists(Path.GetPathRoot(HistoryRewriteStoragePaths.NormalizeBackupRoot(journal.BackupRoot)))) return false;
        var committed = journal.PackId is { } pack && File.Exists(history.Repository.Paths.GetPackPath(pack));
        if (!committed)
        {
            CleanupOperationTree(journal.OperationId, true, journal.BackupRoot);
            CleanupOperationTree(journal.OperationId, false, journal.BackupRoot);
            Save(journal with { Complete = true });
            return true;
        }
        // The repository journal must have applied Catalog before this recovery phase.
        if (!journal.StateApplied)
        {
            await RepairBaselinesAsync(journal, token).ConfigureAwait(false);
            checkpoint?.Invoke("cache-repaired");
            journal = journal with { StateApplied = true };
            Save(journal);
        }
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false));
        if (catalog.Status != DeviceLocalStateStatus.Valid || catalog.Value is null)
            throw new InvalidDataException("Cannot reclaim archives while Catalog requires recovery.");
        // A crash can be followed by external damage. Never reclaim the fallback bytes if a
        // previously verified retained chain has changed or disappeared since preparation.
        foreach (var witness in journal.VerifiedFiles.IsDefault ? [] : journal.VerifiedFiles)
        {
            if (!Path.IsPathFullyQualified(witness.Path) || !File.Exists(witness.Path)) return false;
            try
            {
                if (new FileInfo(witness.Path).Length != witness.Size
                    || await HashAsync(witness.Path, token).ConfigureAwait(false) != witness.StorageSha256) return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var protectedIds = history.MergeSessions.ProtectedRepresentations(graph);
        var complete = true;
        checkpoint?.Invoke("before-reclamation");
        foreach (var file in journal.RetiredFiles)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(file.Path)) throw new InvalidDataException("Archive reclamation requires an absolute path.");
            // Stable-root registrations cannot be resolved here; conservatively defer reclamation.
            if (catalog.Value.Entries.Any(e => e.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath
                    || StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, file.Path))
                || file.Representations.Any(protectedIds.Contains))
            { complete = false; continue; }
            try
            {
                if (!File.Exists(file.Path)) continue;
                var info = new FileInfo(file.Path);
                if (info.Length != file.Size || !StringComparer.Ordinal.Equals(await HashAsync(file.Path, token).ConfigureAwait(false), file.StorageSha256))
                { complete = false; continue; }
                File.SetAttributes(file.Path, FileAttributes.Normal);
                File.Delete(file.Path);
                checkpoint?.Invoke("file-reclaimed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        }
        CleanupOperationTree(journal.OperationId, false, journal.BackupRoot);
        Save(journal with { Complete = complete });
        return complete;
    }

    private async Task RepairBaselinesAsync(HistoryChainRewriteJournal journal, CancellationToken token)
    {
        var versions = await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false);
        var mapping = journal.Mappings.ToDictionary(m => m.PreviousId);
        foreach (var source in versions.Select(v => v.SourceId).Distinct())
        {
            var baseline = await history.CaptureBaselines.LoadAsync(source, token).ConfigureAwait(false);
            if (baseline is null) continue;
            if (mapping.TryGetValue(baseline.BaseRepresentationId, out var replacement))
            {
                // FileStates describe the historical version, never the current live folder.
                await history.CaptureBaselines.SaveAsync(source,
                    new Capture.SourceCaptureBaselineCandidate(baseline.Revision, replacement.Path,
                        replacement.DeltaDepth, baseline.FileStates, baseline.BoundaryFingerprint),
                    baseline.BaseVersionId, replacement.Replacement, token).ConfigureAwait(false);
            }
            else if (journal.RetiredFiles.Any(f => StringComparer.OrdinalIgnoreCase.Equals(f.Path, baseline.PayloadPath)))
                await history.CaptureBaselines.RemoveAsync(source, baseline.Revision, token).ConfigureAwait(false);
        }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    internal void CleanupOwnedTree(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(history.Repository.Paths.RepositoryRoot, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException("Rewrite cleanup escapes the repository.");
        DeleteTree(full);
    }

    internal void CleanupOperationTree(HistoryTransactionId id, bool payloads)
        => CleanupOperationTree(id, payloads, BackupRoot);

    private void CleanupOperationTree(HistoryTransactionId id, bool payloads, string? destination)
    {
        var path = payloads ? PayloadRootAt(id, destination) : WorkRootAt(id, destination);
        if (destination is null) { CleanupOwnedTree(path); return; } // Existing pre-fix journals.
        var operationRoot = HistoryRewriteStoragePaths.OperationRoot(destination, history.ConfigId, id);
        if (!HistoryRewriteStoragePaths.IsWithin(path, operationRoot)
            || StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), operationRoot))
            throw new InvalidDataException("Rewrite cleanup escapes its owned operation directory.");
        DeleteTree(path);
    }

    private static void DeleteTree(string full)
    {
        HistoryRewriteStoragePaths.RequireUnlinkedAncestors(full);
        if (!Directory.Exists(full)) return;
        var directories = new System.Collections.Generic.Stack<string>();
        directories.Push(full);
        while (directories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Rewrite cleanup refuses linked directories.");
            foreach (var child in Directory.EnumerateDirectories(directory)) directories.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory)) File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(full, recursive: true);
    }

    private static HistoryChainRewriteJournal Read(string path)
        => JsonSerializer.Deserialize<HistoryChainRewriteJournal>(File.ReadAllBytes(path), Json)
           ?? throw new InvalidDataException("Invalid chain rewrite journal.");
}
