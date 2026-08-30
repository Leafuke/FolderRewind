using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryCheckoutService
{
    private readonly HistoryRuntime _history;
    private readonly HistoryRestoreService _restore;
    private readonly IHistoryWorkingStateProtector? _protector;
    private readonly HistoryCheckoutPlanner _planner;

    public HistoryCheckoutService(
        HistoryRuntime history,
        HistoryRestoreService restore,
        IHistoryWorkingStateProtector? protector = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _protector = protector;
        _planner = new HistoryCheckoutPlanner(_history, _restore);
    }

    public async Task<HistoryRestoreResult> CheckoutAsync(
        BranchUpdateId selectedTipId,
        IReadOnlyList<HistoryRestoreSourceBinding> currentConfigSources,
        HistoryWorkspace expectedWorkspace,
        HistoryCheckoutProtectionMode protectionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentConfigSources);
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        await _restore.RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false);

        HistoryCheckoutPlan? plan = null;
        try
        {
            plan = await _planner.BuildAsync(
                selectedTipId,
                currentConfigSources,
                expectedWorkspace,
                AssessmentDepth.Deep,
                cancellationToken).ConfigureAwait(false);
            if (!plan.CanExecute)
                return Blocked(plan.Diagnostic, plan);
            _ = await _restore.RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
                return Blocked(ex.Message, plan);
        }

        HistoryWorkspace protectedWorkspace;
        try
        {
            if (protectionMode == HistoryCheckoutProtectionMode.ProtectCurrentWork)
            {
                if (_protector is null)
                    return Blocked("Current-work protection is required but no protector is available.");
                protectedWorkspace = await _protector.ProtectAsync(
                    expectedWorkspace,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                protectedWorkspace = expectedWorkspace;
            }
            _ = await _restore.RequireExpectedWorkspaceAsync(protectedWorkspace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Blocked(ex.Message);
        }

        var prepared = new List<HistoryRestoreService.PreparedRestoreSource>();
        try
        {
            foreach (var sourcePlan in plan!.Sources.Where(item => item.Action == HistoryCheckoutSourceAction.Restore))
            {
                var version = await _history.Query.GetVersionAsync(
                    sourcePlan.VersionId!.Value,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Checkpoint SourceVersion is missing.");
                prepared.Add(await _restore.PrepareSourceAsync(
                    version,
                    sourcePlan.Binding!,
                    MaterializationFidelity.Exact,
                    HistoryRestoreApplyMode.Clean,
                    cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }

        await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _restore.RequireExpectedWorkspaceAsync(protectedWorkspace, cancellationToken).ConfigureAwait(false);
            var revalidated = await _planner.BuildAsync(
                selectedTipId,
                currentConfigSources,
                current,
                AssessmentDepth.Deep,
                cancellationToken).ConfigureAwait(false);
            if (!revalidated.CanExecute
                || revalidated.Update!.UpdateId != plan.Update!.UpdateId
                || revalidated.Checkpoint!.CheckpointId != plan.Checkpoint!.CheckpointId)
            {
                throw new InvalidOperationException("Branch selection changed during materialization.");
            }

            var restoredIds = plan!.Sources
                .Where(item => item.Action == HistoryCheckoutSourceAction.Restore)
                .Select(item => item.SourceId)
                .ToHashSet();
            // current-only Source 保留最终 revalidation 时的 baseline；若前面创建过保护点，这里不会回退到旧 request。
            var desiredBaselines = current.SourceBaselines
                .Where(item => !restoredIds.Contains(item.SourceId))
                .Concat(plan.Checkpoint!.Sources.Select(source => new WorkspaceSourceBaseline(
                    source.SourceId,
                    source.VersionId,
                    WorkspaceBaselineRelation.Exact)));
            var desired = new HistoryWorkspace(
                _history.ConfigId,
                checked(current.StateRevision + 1),
                plan.Update!.BranchId,
                plan.Update.UpdateId,
                desiredBaselines);
            return await _restore.ExecuteMutationAsync(
                prepared,
                current,
                desired,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }
    }

    private static HistoryRestoreResult Blocked(
        string diagnostic,
        HistoryCheckoutPlan? plan = null)
        => new(HistoryRestoreStatus.Blocked, diagnostic, false, [], plan);
}
