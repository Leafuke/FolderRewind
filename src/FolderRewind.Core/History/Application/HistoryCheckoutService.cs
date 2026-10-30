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
    private readonly Func<CancellationToken, Task<IReadOnlyList<HistoryRestoreSourceBinding>>>? _reloadBindings;

    public HistoryCheckoutService(
        HistoryRuntime history,
        HistoryRestoreService restore,
        IHistoryWorkingStateProtector? protector = null,
        IHistoryWorkingStateProbe? workingStateProbe = null,
        Func<CancellationToken, Task<IReadOnlyList<HistoryRestoreSourceBinding>>>? reloadBindings = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _protector = protector;
        _reloadBindings = reloadBindings;
        _planner = new HistoryCheckoutPlanner(_history, _restore, workingStateProbe);
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
        using var inputScope = _restore.EnterInputScope();
        await using var ownedOperation = _restore.ExistingOperation is null
            ? await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false) : null;
        var operation = _restore.ExistingOperation ?? ownedOperation!;
        await _restore.RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        HistoryCheckoutPlan? plan = null;
        var prepared = new List<HistoryRestoreService.PreparedRestoreSource>();
        var evidence = new List<HistoryWorkingStateEvidence>();
        var originalProtection = _restore.HoldCurrentReadProtection;
        var originalValidation = _restore.ValidateCurrentInputs;
        var originalUnchanged = _restore.UnchangedTargetSources;
        try
        {
            plan = await _planner.BuildAsync(selectedTipId, currentConfigSources, expectedWorkspace,
                AssessmentDepth.Fast, cancellationToken).ConfigureAwait(false);
            if (!plan.CanExecute) return Blocked(plan.Diagnostic, plan);
            _ = await _restore.RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            var selections = new List<(SourceVersion Version, HistoryRestoreSourceBinding Binding, MaterializationFidelity Fidelity)>();
            foreach (var source in plan.Sources.Where(s => s.Action == HistoryCheckoutSourceAction.Restore))
            {
                var version = await _history.Query.GetVersionAsync(source.VersionId!.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Checkpoint SourceVersion is missing.");
                selections.Add((version, source.Binding!, MaterializationFidelity.Exact));
            }
            using var session = _restore.TryCreatePreparationSession(selections.Select(s => s.Binding).ToArray(), expectedWorkspace, out var preparationError);
            if (preparationError is not null) return Blocked(preparationError);
            await _restore.ReservePreparationSpaceAsync(selections, session, cancellationToken).ConfigureAwait(false);
            foreach (var source in selections)
                prepared.Add(await _restore.PrepareSourceAsync(source.Version, source.Binding, MaterializationFidelity.Exact,
                    HistoryRestoreApplyMode.Clean, cancellationToken, session).ConfigureAwait(false));

            bool needsProtection = false;
            if (protectionMode == HistoryCheckoutProtectionMode.ProtectCurrentWork)
            {
                var probe = new HistoryExactWorkingStateProbe(_history, _restore);
                foreach (var source in prepared)
                {
                    var baseline = expectedWorkspace.SourceBaselines.SingleOrDefault(s => s.SourceId == source.Binding.SourceId);
                    var compared = baseline is null
                        ? new HistoryWorkingStateEvidence(new(HistoryWorkingStateComparisonStatus.Unknown, "BaselineUnavailable"))
                        : await probe.CompareWithEvidenceAsync(source.Binding, baseline, cancellationToken, source).ConfigureAwait(false);
                    evidence.Add(compared);
                    needsProtection |= !compared.Comparison.IsEqual;
                }
            }
            var protectedWorkspace = expectedWorkspace;
            if (needsProtection)
            {
                foreach (var held in evidence) held.Dispose();
                evidence.Clear();
                if (_protector is null) return Blocked("Current-work protection is required but no protector is available.");
                session?.ValidateBindings();
                protectedWorkspace = _protector is IHistoryWorkingStateProtectorInsideOperation inside
                    ? await inside.ProtectInsideOperationAsync(expectedWorkspace, operation, cancellationToken, prepared.Select(p => p.Binding).ToArray()).ConfigureAwait(false)
                    : await _protector.ProtectAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            }
            session?.RebindExpected(protectedWorkspace);
            await using var guard = await _restore.EnterFinalGuardAsync(cancellationToken, operation).ConfigureAwait(false);
            await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var current = await _restore.RequireExpectedWorkspaceAsync(protectedWorkspace, cancellationToken).ConfigureAwait(false);
            var authoritativeBindings = _reloadBindings is null ? currentConfigSources
                : await _reloadBindings(cancellationToken).ConfigureAwait(false);
            if (authoritativeBindings.Count != currentConfigSources.Count
                || currentConfigSources.Any(expected => !authoritativeBindings.Any(actual =>
                    actual.SourceId == expected.SourceId
                    && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actual.TargetDirectory), Path.GetFullPath(expected.TargetDirectory))
                    && actual.Boundary.Fingerprint == expected.Boundary.Fingerprint)))
                throw new InvalidOperationException("Configuration bindings changed during materialization.");
            var revalidated = await _planner.BuildAsync(selectedTipId, authoritativeBindings, current,
                AssessmentDepth.Fast, cancellationToken).ConfigureAwait(false);
            if (!revalidated.CanExecute || revalidated.Update!.UpdateId != plan.Update!.UpdateId
                || revalidated.Checkpoint!.CheckpointId != plan.Checkpoint!.CheckpointId)
                throw new InvalidOperationException("Branch selection changed during materialization.");
            foreach (var held in evidence) held.Validate();
            _restore.HoldCurrentReadProtection = evidence.Count > 0;
            _restore.UnchangedTargetSources = evidence.Count == prepared.Count ? prepared
                .Where(s => expectedWorkspace.GetSourceState(s.Binding.SourceId).BaseVersionId == s.Version.VersionId)
                .Select(s => s.Binding.SourceId).ToHashSet() : new HashSet<SourceId>();
            _restore.ValidateCurrentInputs = () =>
            {
                originalValidation?.Invoke();
                foreach (var held in evidence) held.Validate();
            };
            var sourceState = current.GetSourceState(plan.Checkpoint!.SourceId) with
            {
                ContentRealizationId = Guid.NewGuid(), BaseVersionId = plan.Checkpoint.VersionId,
                Relation = WorkspaceBaselineRelation.Exact, RequiresFullCapture = false,
                ActiveBranchId = plan.Update!.BranchId, ActiveBranchUpdateId = plan.Update.UpdateId,
                CheckpointAncestryAnchorId = plan.Checkpoint.CheckpointId
            };
            return await _restore.ExecuteMutationAsync(prepared, current, current.WithSourceStates([sourceState]),
                cancellationToken, preparationSession: session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(s => s.StagingDirectory));
            return Blocked(ex.Message, plan);
        }
        finally
        {
            _restore.HoldCurrentReadProtection = originalProtection;
            _restore.ValidateCurrentInputs = originalValidation;
            _restore.UnchangedTargetSources = originalUnchanged;
            foreach (var held in evidence) held.Dispose();
        }
    }

    private static HistoryRestoreResult Blocked(
        string diagnostic,
        HistoryCheckoutPlan? plan = null)
        => new(HistoryRestoreStatus.BlockedBeforeMutation, diagnostic, false, [], plan);
}
