using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryMigrationInitializationService(HistoryRuntime runtime)
{
    public async Task InitializeAsync(IEnumerable<SourceId> confirmedSources, CancellationToken token = default)
    {
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(runtime.ConfigId, token).ConfigureAwait(false);
        await InitializeInsideOperationAsync(confirmedSources, operation, [], token).ConfigureAwait(false);
    }

    internal async Task InitializeInsideOperationAsync(IEnumerable<SourceId> confirmedSources,
        NativeHistoryConfigurationOperationGate.Lease operation, IEnumerable<SourceId> resetSources,
        CancellationToken token = default)
    {
        operation.Require(runtime.ConfigId);
        var sources = confirmedSources.Distinct().ToArray();
        var resets = resetSources.Distinct().ToArray();
        if (sources.Length == 0 || resets.Any(id => !sources.Contains(id)))
            throw new ArgumentException("Migration requires confirmed source identities.");
        await using var lease = await runtime.MutationGate.EnterForRecoveryAsync(token).ConfigureAwait(false);
        runtime.Repository.Imports.RequireRecovered();
        runtime.Repository.Journals.RequireRecovered();
        HistoryRestoreTransactionJournalStore.RequireRecovered(runtime.Repository.Paths.TransactionsRoot);
        Retention.HistoryChainRewriteJournalStore.RequireRecovered(runtime.Repository.Paths.LocalStateRoot);
        var migration = runtime.MigrationState;
        if (migration.Load() is null) throw new DeviceLocalStateConflictException("Repository has no explicit migration state.");
        var recovery = new HistoryLocalStateJournalRecovery(runtime.WorkspaceStore, runtime.LocalReplicaCatalogStore);
        await migration.RecoverAsync(recovery, token).ConfigureAwait(false);
        var workspace = await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false);
        var catalog = await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        if (workspace.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible
            || catalog.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new DeviceLocalStateConflictException("Damaged local state must be recovered, not replaced by migration setup.");
        var intents = ImmutableArray.CreateBuilder<Storage.HistoryLocalStateIntent>();
        if (workspace.Status == DeviceLocalStateStatus.Missing)
            intents.Add(HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(new(runtime.ConfigId, 0,
                sources.Select(id => new WorkspaceSourceBaseline(id, null, WorkspaceBaselineRelation.Unknown))), -1));
        else if (resets.Length > 0)
            intents.Add(HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(workspace.Value!.WithSourceStates(
                resets.Select(id => new WorkspaceSourceBaseline(id, null, WorkspaceBaselineRelation.Unknown))), workspace.Value.StateRevision));
        if (catalog.Status == DeviceLocalStateStatus.Missing)
            intents.Add(HistoryLocalStateJournalRecovery.CreateCatalogIntent(new(runtime.ConfigId, 0, []), -1));
        if (intents.Count > 0)
        {
            migration.Prepare(intents.ToImmutable());
            // Once the local initialization decision is durable, cancellation cannot split its two files.
            await migration.RecoverAsync(recovery, CancellationToken.None).ConfigureAwait(false);
        }
        else migration.ConfirmInitialized();
        await runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        runtime.ChangeFeed.Publish(runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
    }
}
