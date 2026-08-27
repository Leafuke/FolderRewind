using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Storage;

public enum HistoryTransactionPhase
{
    Prepared = 0,
    PackInstalled = 1,
    LocalStateApplied = 2,
    Complete = 3
}

public sealed record HistoryLocalStateIntent(
    string StateKind,
    long ExpectedRevision,
    JsonElement Delta);

public sealed record HistoryTransactionJournal(
    HistoryTransactionId TransactionId,
    PackId IntendedPackId,
    HistoryTransactionPhase Phase,
    ImmutableArray<HistoryLocalStateIntent> LocalStateIntents,
    ImmutableArray<string> StagedPayloadCleanupHandles)
{
    public static HistoryTransactionJournal Prepared(
        HistoryTransactionId transactionId,
        PackId intendedPackId,
        IEnumerable<HistoryLocalStateIntent>? localStateIntents = null,
        IEnumerable<string>? stagedPayloadCleanupHandles = null)
        => new(
            transactionId,
            intendedPackId,
            HistoryTransactionPhase.Prepared,
            localStateIntents is null ? [] : [.. localStateIntents],
            stagedPayloadCleanupHandles is null ? [] : [.. stagedPayloadCleanupHandles]);
}

public sealed class HistoryTransactionJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HistoryRepositoryPaths _paths;

    public HistoryTransactionJournalStore(HistoryRepositoryPaths paths)
        => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public void Save(HistoryTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        AtomicFileService.Write(
            _paths.GetJournalPath(journal.TransactionId),
            stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
    }

    public HistoryTransactionJournal Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<HistoryTransactionJournal>(stream, JsonOptions)
            ?? throw new HistoryRepositoryValidationException($"Journal '{path}' is empty.");
    }

    public IReadOnlyList<string> EnumerateJournalPaths()
        => Directory.Exists(_paths.TransactionsRoot)
            ? Directory.GetFiles(_paths.TransactionsRoot, "journal.json", SearchOption.AllDirectories)
            : [];

    public async Task RecoverAsync(
        Func<HistoryTransactionJournal, CancellationToken, Task> applyCommittedState,
        Func<HistoryTransactionJournal, CancellationToken, Task> rollbackPreparedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyCommittedState);
        ArgumentNullException.ThrowIfNull(rollbackPreparedState);
        foreach (var path in EnumerateJournalPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = Load(path);
            if (journal.Phase == HistoryTransactionPhase.Complete)
            {
                continue;
            }

            if (File.Exists(_paths.GetPackPath(journal.IntendedPackId)))
            {
                await applyCommittedState(journal, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await rollbackPreparedState(journal, cancellationToken).ConfigureAwait(false);
            }

            Save(journal with { Phase = HistoryTransactionPhase.Complete });
        }
    }
}

