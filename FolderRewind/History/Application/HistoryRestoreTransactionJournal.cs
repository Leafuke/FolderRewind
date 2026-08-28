using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
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
    ImmutableArray<SourceId> AppliedSources);

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

            var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            bool workspaceCommitted = journal.Phase >= HistoryRestoreTransactionPhase.WorkspaceApplying
                && workspaceLoad.Value is not null
                && WorkspaceEquals(workspaceLoad.Value, journal.DesiredWorkspace);
            if (workspaceCommitted)
            {
                foreach (var snapshot in journal.RollbackSnapshots)
                    await _backend.CommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var snapshot in journal.RollbackSnapshots.Reverse())
                    await _backend.RollbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            CleanupStaging(journal.StagingDirectories);
            Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
            recovered = true;
        }
        return recovered;
    }

    public static bool WorkspaceEquals(HistoryWorkspace left, HistoryWorkspace right)
        => left.ConfigId == right.ConfigId
            && left.StateRevision == right.StateRevision
            && left.ActiveBranchId == right.ActiveBranchId
            && left.ActiveBranchUpdateId == right.ActiveBranchUpdateId
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
