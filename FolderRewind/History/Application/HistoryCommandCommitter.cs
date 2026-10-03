using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

internal sealed record HistoryCommandCommitResult(
    HistoryCommitPack Pack,
    bool IndexRefreshSucceeded);

/// <summary>
/// Shared post-validation transaction primitive for append-only History commands.
/// The caller must hold the Runtime mutation gate while preparing facts and invoking this method.
/// </summary>
internal static class HistoryCommandCommitter
{
    public static async Task<HistoryCommandCommitResult> CommitInsideGateAsync(
        HistoryRuntime runtime,
        HistoryPackCodec codec,
        IEnumerable<object> facts,
        HistoryWorkspace? currentWorkspace,
        HistoryWorkspace? updatedWorkspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(codec);
        var objects = (facts ?? throw new ArgumentNullException(nameof(facts)))
            .Select(fact => codec.CreateObject(fact))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (objects.Length == 0) throw new ArgumentException("A History command must append at least one fact.", nameof(facts));

        var pack = new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            objects);
        var intents = updatedWorkspace is null
            ? Array.Empty<HistoryLocalStateIntent>()
            :
            [
                HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(
                    updatedWorkspace,
                    currentWorkspace?.StateRevision ?? HistoryWorkspaceStore.MissingRevision)
            ];
        var journal = HistoryTransactionJournal.Prepared(pack.TransactionId, pack.PackId, intents);
        await runtime.Repository.CommitAsync(pack, journal, cancellationToken).ConfigureAwait(false);

        try
        {
            var recovery = new HistoryLocalStateJournalRecovery(
                runtime.WorkspaceStore,
                runtime.LocalReplicaCatalogStore);
            await recovery.ApplyCommittedStateAsync(journal, cancellationToken).ConfigureAwait(false);
            runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.LocalStateApplied });
            runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
        }
        catch (Exception ex)
        {
            throw new HistoryCommitRecoveryRequiredException(
                pack.PackId,
                "History command facts are durable, but Workspace state requires journal recovery.",
                ex);
        }

        await runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        bool indexRefreshSucceeded;
        try
        {
            var packs = await runtime.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
            await runtime.Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
            indexRefreshSucceeded = true;
        }
        catch
        {
            indexRefreshSucceeded = false;
        }

        runtime.ChangeFeed.Publish(
            runtime.ConfigId,
            HistoryChangeKind.TransactionCommitted,
            objects.Select(item => item.Id));
        if (updatedWorkspace is not null)
        {
            runtime.ChangeFeed.Publish(runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
        }
        if (indexRefreshSucceeded)
        {
            runtime.ChangeFeed.Publish(runtime.ConfigId, HistoryChangeKind.IndexRebuilt);
        }
        return new HistoryCommandCommitResult(pack, indexRefreshSucceeded);
    }
}
