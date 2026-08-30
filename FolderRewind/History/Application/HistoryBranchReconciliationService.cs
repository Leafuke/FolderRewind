using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryBranchReconciliationService
{
    private const string IdentityDomain = "folderrewind/branch-reconciliation/v1";
    private readonly HistoryRuntime _runtime;
    private readonly HistoryPackCodec _codec;

    public HistoryBranchReconciliationService(HistoryRuntime runtime, HistoryPackCodec? codec = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _codec = codec ?? new HistoryPackCodec();
    }

    public async Task<HistoryBranchCommandResult> ReconcileAsync(
        BranchId branchId,
        IEnumerable<BranchUpdateId> expectedTipIds,
        BranchUpdateId? selectedWinnerTipId = null,
        CancellationToken cancellationToken = default)
    {
        var expected = expectedTipIds?
            .Distinct()
            .OrderBy(item => item.ToString(), StringComparer.Ordinal)
            .ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(expectedTipIds));
        if (expected.Length < 2)
            throw new HistoryBranchCommandException("Branch reconciliation requires at least two expected tips.");

        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var updates = await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var actualTips = HistoryBranchProjection.FindLocalTips(updates)
            .Where(item => item.BranchId == branchId)
            .OrderBy(item => item.UpdateId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();
        if (!expected.SequenceEqual(actualTips.Select(item => item.UpdateId)))
            throw new HistoryBranchCommandException("Branch tips changed; rebuild the reconciliation plan.");

        var equivalent = actualTips.All(item => EquivalentState(item, actualTips[0]));
        BranchUpdate winner;
        if (equivalent)
        {
            winner = actualTips[0];
        }
        else
        {
            if (selectedWinnerTipId is null)
                throw new HistoryBranchCommandException("Non-equivalent tips require an explicit winner.");
            winner = actualTips.SingleOrDefault(item => item.UpdateId == selectedWinnerTipId.Value)
                ?? throw new HistoryBranchCommandException("Selected winner is not in the expected current tip set.");
        }

        var createdAtUtc = actualTips.Max(item => item.CreatedAtUtc).ToUniversalTime();
        var updateId = BranchUpdateId.FromGuid(DeterministicHistoryId.Create(
            IdentityDomain,
            [
                branchId.ToString(),
                string.Join(",", expected),
                winner.TargetCheckpointId?.ToString() ?? string.Empty,
                winner.Name,
                winner.IsDeleted ? "1" : "0",
                ((int)BranchUpdateReason.Reconciled).ToString(CultureInfo.InvariantCulture),
                createdAtUtc.ToString("O", CultureInfo.InvariantCulture)
            ]));
        var reconciliation = new BranchUpdate(
            updateId,
            branchId,
            expected,
            winner.Name,
            winner.TargetCheckpointId,
            winner.IsDeleted,
            createdAtUtc,
            BranchUpdateReason.Reconciled);
        HistoryDomainValidator.ValidateNative(reconciliation);

        var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (workspaceLoad.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new HistoryBranchCommandException("Workspace recovery is required before reconciliation.");
        var workspace = workspaceLoad.Value;
        HistoryWorkspace? updatedWorkspace = null;
        if (workspace?.ActiveBranchId == branchId
            && workspace.ActiveBranchUpdateId is { } activeId
            && actualTips.SingleOrDefault(item => item.UpdateId == activeId) is { } activeTip
            && EquivalentState(activeTip, winner))
        {
            updatedWorkspace = new HistoryWorkspace(
                _runtime.ConfigId,
                checked(workspace.StateRevision + 1),
                branchId,
                reconciliation.UpdateId,
                workspace.SourceBaselines);
        }

        var committed = await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime,
            _codec,
            [reconciliation],
            updatedWorkspace is null ? null : workspace,
            updatedWorkspace,
            cancellationToken).ConfigureAwait(false);
        return new(
            committed.Pack.PackId,
            reconciliation,
            null,
            updatedWorkspace is not null,
            committed.IndexRefreshSucceeded);
    }

    private static bool EquivalentState(BranchUpdate left, BranchUpdate right)
        => left.TargetCheckpointId == right.TargetCheckpointId
            && StringComparer.Ordinal.Equals(left.Name, right.Name)
            && left.IsDeleted == right.IsDeleted;
}
