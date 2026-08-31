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
        if (workspace?.ActiveBranchId is not { } branchId
            || workspace.ActiveBranchUpdateId is not { } anchorId)
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
        var assessment = await _restore.AssessVersionAsync(
            versionId.Value,
            MaterializationFidelity.Exact,
            assessmentDepth,
            cancellationToken).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            return new(
                HistoryQuickRestoreResolutionStatus.RepresentationNotReady,
                versionId,
                anchorId,
                assessment.Readiness,
                "Active Branch candidate is not Ready with Exact fidelity.");
        return new(
            HistoryQuickRestoreResolutionStatus.Ready,
            versionId,
            anchorId,
            assessment.Readiness,
            string.Empty);
    }

    private static HistoryQuickRestoreResolution Missing(
        string diagnostic,
        BranchUpdateId? anchorId = null)
        => new(HistoryQuickRestoreResolutionStatus.NoRestoreCandidate, null, anchorId, null, diagnostic);
}
