using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryQuickRestoreResolutionStatus
{
    Ready = 0,
    NoRestoreCandidate = 1,
    BranchReconciliationRequired = 2,
    RepresentationNotReady = 3
}

public sealed record HistoryQuickRestoreResolution(
    HistoryQuickRestoreResolutionStatus Status,
    VersionId? VersionId,
    BranchUpdateId? AnchorUpdateId,
    HistoryReadiness? Readiness,
    string Diagnostic)
{
    public bool IsReady => Status == HistoryQuickRestoreResolutionStatus.Ready;
    public BranchId? ActiveBranchId { get; init; }
}

public sealed class HistoryQuickRestoreResolver
{
    private readonly HistoryRuntime _history;
    private readonly HistoryRestoreService _restore;

    public HistoryQuickRestoreResolver(HistoryRuntime history, HistoryRestoreService restore)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
    }

    public async Task<HistoryQuickRestoreResolution> ResolveAsync(
        SourceId sourceId,
        AssessmentDepth assessmentDepth,
        CancellationToken cancellationToken = default)
    {
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var workspace = (await _history.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        if (workspace is null)
            return Missing("Workspace requires recovery before restore.");
        var state = workspace.GetSourceState(sourceId);
        if (state.ActiveBranchId is not { } branchId)
            return await ResolveLegacyAsync(sourceId, assessmentDepth, cancellationToken).ConfigureAwait(false);
        if (state.ActiveBranchUpdateId is not { } anchorId)
            return Missing("Workspace has no active Branch anchor.");
        var tips = await _history.Query.GetBranchTipsAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (tips.Count != 1 || tips[0].UpdateId != anchorId)
            return new(
                HistoryQuickRestoreResolutionStatus.BranchReconciliationRequired,
                null,
                anchorId,
                null,
                "Active Branch is multi-tip or the Workspace anchor is stale.");
        var tip = tips[0];
        if (tip.IsDeleted || tip.TargetCheckpointId is not { } checkpointId)
            return Missing("Active Branch tip has no checkpoint.", anchorId);
        var checkpoint = await _history.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        var versionId = checkpoint?.Sources.SingleOrDefault(item => item.SourceId == sourceId)?.VersionId;
        if (versionId is null)
            return Missing("Active Branch checkpoint has no Version for this Source.", anchorId);
        var version = await _history.Query.GetVersionAsync(versionId.Value, cancellationToken).ConfigureAwait(false);
        if (version is null || version.SourceId != sourceId)
            return Missing("Active Branch Version does not exist or belongs to another Source.", anchorId);
        var fidelity = HistoryRecoveryPreview.FidelityFor(version);
        var assessment = await _restore.AssessVersionAsync(
            versionId.Value,
            fidelity,
            assessmentDepth,
            cancellationToken).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            return new(
                HistoryQuickRestoreResolutionStatus.RepresentationNotReady,
                versionId,
                anchorId,
                assessment.Readiness,
                AssessmentDiagnostic(assessment, "Active Branch candidate is not recoverable."));
        return new(
            HistoryQuickRestoreResolutionStatus.Ready,
            versionId,
            anchorId,
            assessment.Readiness,
            string.Empty) { ActiveBranchId = branchId };
    }

    private async Task<HistoryQuickRestoreResolution> ResolveLegacyAsync(SourceId sourceId,
        AssessmentDepth depth, CancellationToken token)
    {
        var timeline = (await new HistoryPresentationQueryService(_history).QueryAsync(sourceId,
            cancellationToken: token).ConfigureAwait(false)).Timeline;
        string? lastDiagnostic = null;
        foreach (var entry in timeline.Where(e => !e.IsReleased && !e.IsSuppressed)
                     .OrderByDescending(e => e.CreatedAtUtc)
                     .ThenByDescending(e => e.VersionId.ToString(), StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var version = await _history.Query.GetVersionAsync(entry.VersionId, token).ConfigureAwait(false);
            if (version?.BoundaryConfidence != HistoricalBoundaryConfidence.Unknown) continue;
            var assessment = await _restore.AssessVersionAsync(entry.VersionId, MaterializationFidelity.Partial,
                depth, token).ConfigureAwait(false);
            if (assessment.Readiness == HistoryReadiness.Ready && assessment.Selected is not null)
                return new(HistoryQuickRestoreResolutionStatus.Ready, entry.VersionId, null,
                    HistoryReadiness.Ready, "Selected the latest recoverable legacy backup because this Source has no active Branch.");
            lastDiagnostic = AssessmentDiagnostic(assessment, "Legacy backup is not recoverable.");
        }
        return Missing("No recoverable legacy backup is available for this Source. " + lastDiagnostic);
    }

    internal async Task ValidateAsync(SourceId sourceId, HistoryQuickRestoreResolution selection, CancellationToken token)
    {
        await _history.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        var workspace = (await _history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value
            ?? throw new InvalidOperationException("Workspace requires recovery before restore.");
        var state = workspace.GetSourceState(sourceId);
        if (state.ActiveBranchId != selection.ActiveBranchId || state.ActiveBranchUpdateId != selection.AnchorUpdateId)
            throw new InvalidOperationException("Active Branch changed after the restore target was selected; request restore again.");
        if (selection.ActiveBranchId is not { } branch) return;
        var tips = await _history.Query.GetBranchTipsAsync(branch, token).ConfigureAwait(false);
        if (tips.Count != 1 || tips[0].UpdateId != selection.AnchorUpdateId
            || tips[0].IsDeleted || tips[0].TargetCheckpointId is not { } checkpointId)
            throw new InvalidOperationException("Active Branch is multi-tip or its selected restore anchor is stale.");
        var checkpoint = await _history.Query.GetCheckpointAsync(checkpointId, token).ConfigureAwait(false);
        if (checkpoint?.Sources.SingleOrDefault(s => s.SourceId == sourceId)?.VersionId != selection.VersionId)
            throw new InvalidOperationException("Active Branch restore target changed after selection.");
    }

    internal static string AssessmentDiagnostic(VersionAssessment assessment, string fallback)
    {
        var diagnostics = assessment.Candidates.SelectMany(c => c.Diagnostics)
            .Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();
        return diagnostics.Length == 0 ? fallback : fallback + " " + string.Join("; ", diagnostics);
    }

    private static HistoryQuickRestoreResolution Missing(
        string diagnostic,
        BranchUpdateId? anchorId = null)
        => new(HistoryQuickRestoreResolutionStatus.NoRestoreCandidate, null, anchorId, null, diagnostic);
}
