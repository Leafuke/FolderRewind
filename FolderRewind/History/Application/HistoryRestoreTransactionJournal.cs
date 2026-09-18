using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

internal enum HistoryRestoreTransactionPhase
{
    Prepared = 0,
    Mutating = 1,
    WorkspaceApplying = 2,
    WorkspaceApplied = 3,
    Complete = 4
}

internal sealed record HistoryRestoreTransactionJournal(
    HistoryTransactionId TransactionId,
    HistoryRestoreTransactionPhase Phase,
    HistoryWorkspace ExpectedWorkspace,
    HistoryWorkspace DesiredWorkspace,
    ImmutableArray<string> StagingDirectories,
    ImmutableArray<HistoryRestoreRollbackSnapshot> RollbackSnapshots,
    ImmutableArray<SourceId> AppliedSources,
    ImmutableArray<SourceId> StartedSources = default,
    byte[]? IntendedPackBytes = null,
    LocalReplicaCatalog? DesiredCatalog = null,
    long ExpectedCatalogRevision = -1);

internal sealed class HistoryRestoreTransactionJournalStore
{
    private const string FileName = "restore-journal.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HistoryRuntime _runtime;
    private readonly IHistoryRestoreMutationBackend _backend;

    public HistoryRestoreTransactionJournalStore(
        HistoryRuntime runtime,
        IHistoryRestoreMutationBackend backend)
    {
        _runtime = runtime;
        _backend = backend;
    }

    internal static void RequireRecovered(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories))
        {
            var journal = JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(path), JsonOptions);
            if (journal is null || journal.Phase != HistoryRestoreTransactionPhase.Complete)
                throw new InvalidOperationException("An incomplete workspace transaction requires recovery before further mutations or GC.");
        }
    }

    public void Save(HistoryRestoreTransactionJournal journal)
    {
        var path = GetPath(journal.TransactionId);
        AtomicFileService.Write(
            path,
            stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
    }

    public async Task<bool> RecoverIncompleteAsync(CancellationToken cancellationToken)
    {
        var root = _runtime.Repository.Paths.TransactionsRoot;
        if (!Directory.Exists(root)) return false;
        bool recovered = false;
        foreach (var path in Directory.GetFiles(root, FileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                JsonOptions) ?? throw new InvalidDataException($"Restore journal '{path}' is empty.");
            if (journal.Phase == HistoryRestoreTransactionPhase.Complete) continue;
            if (journal.StartedSources.IsDefault && journal.Phase == HistoryRestoreTransactionPhase.Mutating && !journal.RollbackSnapshots.IsEmpty)
                throw new InvalidDataException("Restore journal lacks source progress; explicit recovery is required.");

            var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            bool workspaceCommitted = journal.Phase >= HistoryRestoreTransactionPhase.WorkspaceApplying
                && workspaceLoad.Value is not null
                && WorkspaceEquals(workspaceLoad.Value, journal.DesiredWorkspace);
            if (journal.IntendedPackBytes is not null)
            {
                workspaceCommitted = IsPackCommitted(journal);
                if (workspaceCommitted) await CompleteLocalStateAsync(journal, cancellationToken).ConfigureAwait(false);
            }
            if (workspaceCommitted)
            {
                foreach (var snapshot in journal.RollbackSnapshots)
                    await _backend.CommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var snapshot in journal.RollbackSnapshots.Where(s => !journal.StartedSources.IsDefault
                    && journal.StartedSources.Contains(s.SourceId)).Reverse())
                    await _backend.RollbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            CleanupStaging(journal.StagingDirectories);
            Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
            recovered = true;
        }
        if (Directory.Exists(_runtime.MergeSessions.Root))
            foreach (var session in _runtime.MergeSessions.List().Where(s => s.State == MergeSessionState.Applying))
            {
                var journalPath = GetPath(session.ApplyTransactionId!.Value);
                if (File.Exists(journalPath))
                {
                    var intent = JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(journalPath), JsonOptions)
                        ?? throw new InvalidDataException("Merge journal is empty.");
                    if (intent.Phase != HistoryRestoreTransactionPhase.Complete) throw new InvalidDataException("Merge recovery is incomplete.");
                    _runtime.MergeSessions.Update(session, IsPackCommitted(intent) ? MergeSessionState.Committed : MergeSessionState.Ready);
                }
                else
                {
                    if (File.Exists(_runtime.Repository.Paths.GetPackPath(session.IntendedPackId!.Value)))
                        throw new InvalidDataException("Committed Merge is missing its recovery journal.");
                    _runtime.MergeSessions.Update(session, MergeSessionState.Ready);
                }
            }
        return recovered;
    }

    public bool IsPackCommitted(HistoryRestoreTransactionJournal journal)
    {
        if (journal.IntendedPackBytes is null) return false;
        var codec = new HistoryPackCodec(); var expected = codec.Decode(journal.IntendedPackBytes).Pack;
        var path = _runtime.Repository.Paths.GetPackPath(expected.PackId);
        if (!File.Exists(path)) return false;
        var bytes = File.ReadAllBytes(path); _ = codec.Decode(bytes);
        if (!bytes.AsSpan().SequenceEqual(journal.IntendedPackBytes)) throw new InvalidDataException("Durable Merge pack differs from transaction intent.");
        return true;
    }

    public async Task CompleteLocalStateAsync(HistoryRestoreTransactionJournal journal, CancellationToken token)
    {
        if (journal.DesiredCatalog is { } desired)
        {
            var catalog = (await _runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
            if (catalog is null || JsonSerializer.Serialize(catalog, JsonOptions) != JsonSerializer.Serialize(desired, JsonOptions))
                await _runtime.LocalReplicaCatalogStore.SaveAsync(desired, journal.ExpectedCatalogRevision, token).ConfigureAwait(false);
        }
        var workspace = (await _runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (workspace is null || !WorkspaceEquals(workspace, journal.DesiredWorkspace))
            await _runtime.WorkspaceStore.SaveAsync(journal.DesiredWorkspace, journal.ExpectedWorkspace.StateRevision, token).ConfigureAwait(false);
    }

    public static bool WorkspaceEquals(HistoryWorkspace left, HistoryWorkspace right)
        => left.ConfigId == right.ConfigId
            && left.StateRevision == right.StateRevision
            && left.ActiveBranchId == right.ActiveBranchId
            && left.ActiveBranchUpdateId == right.ActiveBranchUpdateId
            && left.CheckpointAncestryAnchorId == right.CheckpointAncestryAnchorId
            && left.SourceBaselines.OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal)
                .SequenceEqual(right.SourceBaselines.OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal));

    public static void CleanupStaging(IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch { }
        }
    }

    private string GetPath(HistoryTransactionId transactionId)
    {
        var directory = _runtime.Repository.Paths.GetTransactionDirectory(transactionId);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, FileName);
    }
}
