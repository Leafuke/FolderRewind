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

public enum HistoryCheckoutReadiness
{
    Ready = 0,
    PreparationRequired = 1,
    ProtectionRequired = 2,
    ConfigurationMappingRequired = 3,
    ConfigurationBoundaryChangeRequired = 4,
    ExactRepresentationUnavailable = 5,
    BranchReconciliationRequired = 6,
    StalePlan = 7,
    Blocked = 8
}

public enum HistoryCheckoutSourceAction
{
    Restore = 0,
    PreserveCurrent = 1
}

public sealed record MissingHistoricalSource(
    SourceId SourceId,
    SourceDescriptorSnapshot Descriptor,
    string SuggestedPath);

public sealed record HistorySourceBoundaryMismatch(
    SourceId SourceId,
    EffectiveSourceBoundarySnapshot HistoricalBoundary,
    EffectiveSourceBoundarySnapshot CurrentBoundary);

public sealed record HistoryCheckoutSourcePlan(
    SourceId SourceId,
    HistoryCheckoutSourceAction Action,
    VersionId? VersionId,
    HistoryRestoreSourceBinding? Binding);

public sealed record HistoryCheckoutPlan(
    HistoryCheckoutReadiness Readiness,
    BranchUpdate? Update,
    ConfigurationCheckpoint? Checkpoint,
    long ExpectedWorkspaceRevision,
    ImmutableArray<HistoryCheckoutSourcePlan> Sources,
    ImmutableArray<MissingHistoricalSource> MissingHistoricalSources,
    ImmutableArray<HistorySourceBoundaryMismatch> BoundaryMismatches,
    string Diagnostic)
{
    public bool CanExecute => Readiness is HistoryCheckoutReadiness.Ready
        or HistoryCheckoutReadiness.ProtectionRequired;
    public bool RequiresProtection => Readiness == HistoryCheckoutReadiness.ProtectionRequired;
}

public sealed class HistoryCheckoutPlanner
{
    private readonly HistoryRuntime _history;
    private readonly HistoryRestoreService _restore;

    public HistoryCheckoutPlanner(HistoryRuntime history, HistoryRestoreService restore)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
    }

    public async Task<HistoryCheckoutPlan> BuildAsync(
        BranchUpdateId selectedTipId,
        IReadOnlyList<HistoryRestoreSourceBinding> currentConfigSources,
        HistoryWorkspace expectedWorkspace,
        AssessmentDepth assessmentDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentConfigSources);
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var duplicateIdentity = currentConfigSources.GroupBy(item => item.SourceId).Any(group => group.Count() > 1);
        var duplicatePath = currentConfigSources
            .Select(item => Path.GetFullPath(item.TargetDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != currentConfigSources.Count;
        if (duplicateIdentity || duplicatePath)
            return Blocked(HistoryCheckoutReadiness.Blocked, expectedWorkspace, "Current Config Source bindings are not unique.");

        var update = await _history.Query.GetBranchUpdateAsync(selectedTipId, cancellationToken).ConfigureAwait(false);
        if (update is null || update.IsDeleted || update.TargetCheckpointId is null)
            return Blocked(HistoryCheckoutReadiness.Blocked, expectedWorkspace, "Selected BranchUpdate is missing, deleted, or unborn.");
        var tips = await _history.Query.GetBranchTipsAsync(update.BranchId, cancellationToken).ConfigureAwait(false);
        if (tips.Count != 1 || tips[0].UpdateId != update.UpdateId)
            return Blocked(
                HistoryCheckoutReadiness.BranchReconciliationRequired,
                expectedWorkspace,
                "Selected Branch must have exactly one current local tip before Checkout.",
                update);
        var checkpoint = await _history.Query.GetCheckpointAsync(update.TargetCheckpointId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is null || !checkpoint.IsStructurallyComplete)
            return Blocked(HistoryCheckoutReadiness.Blocked, expectedWorkspace, "Branch target checkpoint is missing or structurally incomplete.", update, checkpoint);

        var bindingMap = currentConfigSources.ToDictionary(item => item.SourceId);
        var missing = checkpoint.Sources
            .Where(source => !bindingMap.ContainsKey(source.SourceId))
            .Select(source => new MissingHistoricalSource(
                source.SourceId,
                source.SourceDescriptorSnapshot,
                source.SourceDescriptorSnapshot.PathHint))
            .ToImmutableArray();
        if (!missing.IsEmpty)
        {
            return new HistoryCheckoutPlan(
                HistoryCheckoutReadiness.ConfigurationMappingRequired,
                update,
                checkpoint,
                expectedWorkspace.StateRevision,
                [],
                missing,
                [],
                "Historical Sources must be restored to the current Config under their original SourceId.");
        }

        var mismatches = checkpoint.Sources
            .Select(source => (Source: source, Binding: bindingMap[source.SourceId]))
            .Where(pair => !StringComparer.Ordinal.Equals(
                pair.Source.EffectiveSourceBoundaryFingerprint,
                pair.Binding.Boundary.Fingerprint))
            .Select(pair => new HistorySourceBoundaryMismatch(
                pair.Source.SourceId,
                pair.Source.EffectiveSourceBoundary,
                pair.Binding.Boundary))
            .ToImmutableArray();
        if (!mismatches.IsEmpty)
        {
            return new HistoryCheckoutPlan(
                HistoryCheckoutReadiness.ConfigurationBoundaryChangeRequired,
                update,
                checkpoint,
                expectedWorkspace.StateRevision,
                [],
                [],
                mismatches,
                "Historical and current Effective Source Boundaries differ and require an explicit Config repair.");
        }

        foreach (var source in checkpoint.Sources)
        {
            var assessment = await _restore.AssessVersionAsync(
                source.VersionId!.Value,
                MaterializationFidelity.Exact,
                assessmentDepth,
                cancellationToken).ConfigureAwait(false);
            if (assessment.Readiness == HistoryReadiness.PreparationRequired)
                return Blocked(HistoryCheckoutReadiness.PreparationRequired, expectedWorkspace, "Exact representation preparation is required.", update, checkpoint);
            if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
                return Blocked(HistoryCheckoutReadiness.ExactRepresentationUnavailable, expectedWorkspace, "An Exact representation is unavailable.", update, checkpoint);
        }

        var historical = checkpoint.Sources.Select(source => new HistoryCheckoutSourcePlan(
            source.SourceId,
            HistoryCheckoutSourceAction.Restore,
            source.VersionId,
            bindingMap[source.SourceId]));
        var historicalIds = checkpoint.Sources.Select(source => source.SourceId).ToHashSet();
        var currentOnly = currentConfigSources
            .Where(binding => !historicalIds.Contains(binding.SourceId))
            .Select(binding => new HistoryCheckoutSourcePlan(
                binding.SourceId,
                HistoryCheckoutSourceAction.PreserveCurrent,
                null,
                binding));
        var requiresProtection = currentConfigSources.Any(binding =>
        {
            var baseline = expectedWorkspace.SourceBaselines.FirstOrDefault(item => item.SourceId == binding.SourceId);
            return baseline is null
                || baseline.BaseVersionId is null
                || baseline.Relation != WorkspaceBaselineRelation.Exact;
        });
        return new HistoryCheckoutPlan(
            requiresProtection ? HistoryCheckoutReadiness.ProtectionRequired : HistoryCheckoutReadiness.Ready,
            update,
            checkpoint,
            expectedWorkspace.StateRevision,
            historical.Concat(currentOnly).ToImmutableArray(),
            [],
            [],
            string.Empty);
    }

    private static HistoryCheckoutPlan Blocked(
        HistoryCheckoutReadiness readiness,
        HistoryWorkspace workspace,
        string diagnostic,
        BranchUpdate? update = null,
        ConfigurationCheckpoint? checkpoint = null)
        => new(readiness, update, checkpoint, workspace.StateRevision, [], [], [], diagnostic);
}
