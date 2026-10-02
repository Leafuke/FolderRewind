using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryPresentationReadiness
{
    Ready = 0,
    PreparationRequired = 1,
    PluginOrCredentialRequired = 2,
    Unavailable = 3,
    PayloadReleased = 4,
    MetadataOnly = 5
}

public sealed record TimelineEntrySummary(
    VersionId VersionId,
    RepresentationId? RepresentationId,
    SourceId SourceId,
    DateTimeOffset CreatedAtUtc,
    string DisplayName,
    string? FileName,
    string? LocalPath,
    string Comment,
    bool IsPinned,
    bool IsSuppressed,
    bool IsReleased,
    CaptureScope CaptureScope,
    HistoryPresentationReadiness Readiness,
    MaterializationFidelity Fidelity,
    ImmutableArray<VersionId> ParentVersionIds,
    ImmutableArray<VersionId> ChildVersionIds,
    ImmutableArray<BranchId> BranchIds,
    CheckpointId? BranchableCheckpointId,
    int BranchableCheckpointCount,
    SourceVersionCreationKind CreationKind = SourceVersionCreationKind.Capture,
    CheckpointId? CheckpointId = null);

public sealed record CheckpointSummary(
    CheckpointId CheckpointId,
    DateTimeOffset CreatedAtUtc,
    bool IsStructurallyComplete,
    bool IsPinned,
    ImmutableArray<CheckpointSource> Sources);

public sealed record RunSummary(
    RunId RunId,
    DateTimeOffset CompletedAtUtc,
    BackupRunOutcome Outcome,
    bool IsImportant,
    string Comment,
    bool HasPartialCapture,
    ImmutableArray<BackupRunSourceResult> Sources,
    ImmutableArray<BranchId> BranchIds,
    IReadOnlyDictionary<BranchUpdateId, string>? HistoricalBranchNames = null, IReadOnlySet<CheckpointId>? BranchableCheckpointIds = null);

public sealed record BranchSummary(
    BranchId BranchId,
    string Name,
    ImmutableArray<BranchUpdate> Tips,
    bool IsUnborn,
    bool IsMultiTip,
    bool IsDeleted,
    bool HasNameCollision,
    bool IsActive,
    bool IsWorkspaceAnchoredAtTip,
    bool HasCheckoutTarget,
    bool CanRename,
    bool CanDelete, SourceId SourceId = default);

public sealed record HistoryPresentationSnapshot(
    ImmutableArray<TimelineEntrySummary> Timeline,
    ImmutableArray<CheckpointSummary> Checkpoints,
    ImmutableArray<RunSummary> Runs,
    ImmutableArray<BranchSummary> Branches,
    ImmutableArray<SafetySnapshotProjection> ActiveSafetySnapshots,
    BranchId? ActiveBranchId,
    BranchUpdateId? ActiveBranchUpdateId);

public sealed class HistoryPresentationQueryService
{
    private readonly HistoryRuntime _runtime;
    private readonly Action<string, TimeSpan>? _measure;

    public HistoryPresentationQueryService(HistoryRuntime runtime, Action<string, TimeSpan>? measure = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _measure = measure;
    }

    public async Task<HistoryPresentationSnapshot> QueryAsync(
        SourceId? sourceId = null,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
    {
        // Only standalone profiling callers request timing; normal UI queries do not.
        var timer = _measure is null ? null : Stopwatch.StartNew();
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        _measure?.Invoke("index-current", timer!.Elapsed);
        timer?.Restart();
        var versions = await _runtime.Query.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        var allRepresentations = await _runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = await _runtime.Query.GetAllCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        var runs = await _runtime.Query.GetRunsAsync(cancellationToken).ConfigureAwait(false);
        var annotations = await _runtime.Query.GetAllAnnotationUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var branchUpdates = await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var migrations = await _runtime.Query.GetMigrationRecordsAsync(cancellationToken).ConfigureAwait(false);
        var policies = await _runtime.Query.GetAllMaterializationPolicyUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var replicas = await _runtime.Query.GetAllStorageReplicasAsync(cancellationToken).ConfigureAwait(false);
        var lifecycle = await _runtime.Query.GetAllReplicaLifecycleUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var catalog = (await _runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var workspace = (await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var safetySnapshots = await _runtime.Query.GetSafetySnapshotProjectionsAsync(
            activeOnly: true,
            cancellationToken).ConfigureAwait(false);
        _measure?.Invoke("index-query", timer!.Elapsed);
        timer?.Restart();
        var policyGroups = policies.ToLookup(item => item.VersionId);
        var replicaGroups = replicas.ToLookup(item => item.RepresentationId);
        var activeReplicas = lifecycle.GroupBy(item => item.ReplicaId).Where(group =>
        {
            var parents = group.SelectMany(item => item.ParentUpdateIds).ToHashSet();
            return group.Any(item => !parents.Contains(item.UpdateId) && item.State == ReplicaLifecycleState.Active);
        }).Select(group => group.Key).ToHashSet();
        var localPaths = (catalog?.Entries ?? []).Where(item => item.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
            .ToLookup(item => item.RepresentationId, item => item.Locator.AbsolutePath);
        var completeCheckpointIds = checkpoints.Where(item => item.IsStructurallyComplete).Select(item => item.CheckpointId).ToHashSet();
        var branchProjection = HistoryBranchProjection.Query(branchUpdates);
        var memberships = HistoryBranchMembershipProjection.Build(branchUpdates, checkpoints);
        var supportIds = migrations.Where(item => item.Visibility == LegacyMigrationVisibility.SupportOnly)
            .Select(item => item.VersionId).ToHashSet();
        var children = versions.SelectMany(item => item.ParentVersionIds.Select(parent => (parent, item.VersionId)))
            .GroupBy(item => item.parent)
            .ToDictionary(group => group.Key, group => group.Select(item => item.VersionId).ToImmutableArray());
        var annotationGroups = annotations.GroupBy(item => item.Target).ToDictionary(group => group.Key, group => group.AsEnumerable());
        var representationGroups = allRepresentations.GroupBy(item => item.VersionId).ToDictionary(group => group.Key, group => group.ToArray());
        var versionScopes = versions.ToDictionary(item => item.VersionId, item => item.CaptureScope);
        var versionMap = versions.ToDictionary(v => v.VersionId);
        var representationMap = allRepresentations.ToDictionary(r => r.RepresentationId);
        var timeline = new List<TimelineEntrySummary>();
        foreach (var checkpoint in checkpoints.Where(c => c.CreationKind != CheckpointCreationKind.SafetySnapshot
                     && (sourceId is null || c.SourceId == sourceId)))
        {
            if (!versionMap.TryGetValue(checkpoint.VersionId, out var version) || supportIds.Contains(version.VersionId)) continue;
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.VersionId.Value);
            var annotation = HistoryAnnotationProjection.Project(
                target,
                annotationGroups.GetValueOrDefault(target) ?? []);
            if (annotation.IsSuppressed && !includeSuppressed) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var policy = MaterializationPolicyProjection.Project(version.VersionId, policyGroups[version.VersionId]);
            var reps = representationGroups.GetValueOrDefault(version.VersionId) ?? [];
            var state = Assess(reps, localPaths, replicaGroups, activeReplicas, policy);
            var selected = state.Representation;
            var localPath = state.LocalPath;
            var fileName = selected?.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
                ?? selected?.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName")
                ?? (localPath is null ? null : Path.GetFileName(localPath));
            var admission = HistoryExactCheckpointAdmission.Evaluate(checkpoint, versionMap, representationMap);
            timeline.Add(new(
                version.VersionId, selected?.RepresentationId, version.SourceId, checkpoint.CreatedAtUtc,
                version.SourceDescriptorSnapshot.DisplayName, fileName, localPath,
                annotation.EffectiveComment ?? string.Empty, annotation.IsPinned, annotation.IsSuppressed,
                policy.HasExplicitPolicy && policy.EffectiveState == MaterializationPolicyState.Released,
                version.CaptureScope, state.Readiness, state.Fidelity, version.ParentVersionIds,
                children.GetValueOrDefault(version.VersionId, []),
                memberships.CheckpointBranches.GetValueOrDefault(checkpoint.CheckpointId, []),
                admission.IsReady ? checkpoint.CheckpointId : null,
                admission.IsReady ? 1 : 0, checkpoint.CreationKind == CheckpointCreationKind.Merge ? SourceVersionCreationKind.Merge : version.CreationKind, checkpoint.CheckpointId));
        }

        var checkpointSummaries = new List<CheckpointSummary>();
        foreach (var checkpoint in checkpoints)
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Checkpoint, checkpoint.CheckpointId.Value);
            var projection = HistoryAnnotationProjection.Project(target, annotationGroups.GetValueOrDefault(target) ?? []);
            checkpointSummaries.Add(new(checkpoint.CheckpointId, checkpoint.CreatedAtUtc, checkpoint.IsStructurallyComplete,
                projection.IsPinned, checkpoint.Sources));
        }
        var historicalBranchNames = branchUpdates.ToDictionary(u => u.UpdateId, u => u.Name);
        var branchableIds = checkpoints.Where(c => HistoryExactCheckpointAdmission.Evaluate(c, versionMap, representationMap).IsReady).Select(c => c.CheckpointId).ToHashSet();
        var runSummaries = runs.Select(run =>
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, run.RunId.Value);
            var projection = HistoryAnnotationProjection.Project(target, annotationGroups.GetValueOrDefault(target) ?? []);
            var hasPartialCapture = run.SourceResults.Any(item => item.VersionId is { } versionId
                    && versionScopes.GetValueOrDefault(versionId) == CaptureScope.PartialSource);
            var branchIds = run.SourceResults.Where(r => r.BranchUpdateId is not null)
                .Select(r => branchUpdates.FirstOrDefault(u => u.UpdateId == r.BranchUpdateId)?.BranchId)
                .OfType<BranchId>().Distinct().ToImmutableArray();
            return new RunSummary(run.RunId, run.CompletedAtUtc, run.Outcome,
                projection.IsRunImportant, projection.EffectiveComment ?? string.Empty,
                hasPartialCapture, run.SourceResults, branchIds, historicalBranchNames, branchableIds);
        }).ToImmutableArray();
        var branchSummaries = branchProjection.Branches.Where(branch => !branch.IsDeleted && (sourceId is null || branch.Tips[0].SourceId == sourceId)).Select(branch =>
        {
            var name = branch.Tips.Where(item => !item.IsDeleted).Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault()
                ?? branch.Tips.FirstOrDefault()?.Name ?? string.Empty;
            bool unborn = branch.Tips.All(item => item.IsUnborn);
            bool canMutate = !branch.IsMultiTip && !branch.IsDeleted;
            bool isActive = workspace?.GetSourceState(branch.Tips[0].SourceId).ActiveBranchId == branch.BranchId;
            bool isWorkspaceAnchoredAtTip = isActive
                && branch.Tips.Length == 1
                && workspace?.GetSourceState(branch.Tips[0].SourceId).ActiveBranchUpdateId == branch.Tips[0].UpdateId;
            return new BranchSummary(branch.BranchId, name, branch.Tips, unborn, branch.IsMultiTip,
                branch.IsDeleted, branch.HasNameCollision,
                isActive,
                isWorkspaceAnchoredAtTip,
                !branch.IsDeleted && !unborn && branch.Tips.Any(item => item.TargetCheckpointId is not null),
                canMutate, canMutate && !isActive, branch.Tips[0].SourceId);
        }).OrderByDescending(branch => branch.IsActive)
            .ThenBy(branch => branch.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(branch => branch.BranchId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();
        var snapshot = new HistoryPresentationSnapshot(
            timeline.OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.VersionId.ToString(), StringComparer.Ordinal).ToImmutableArray(),
            checkpointSummaries.OrderByDescending(item => item.CreatedAtUtc).ToImmutableArray(),
            runSummaries.OrderByDescending(item => item.CompletedAtUtc).ToImmutableArray(),
            branchSummaries,
            safetySnapshots.ToImmutableArray(),
            sourceId is { } activeSource ? workspace?.GetSourceState(activeSource).ActiveBranchId : null,
            sourceId is { } updateSource ? workspace?.GetSourceState(updateSource).ActiveBranchUpdateId : null);
        _measure?.Invoke("metadata-projection", timer!.Elapsed);
        return snapshot;
    }

    private static (
        HistoryPresentationReadiness Readiness,
        MaterializationFidelity Fidelity,
        VersionRepresentation? Representation,
        string? LocalPath) Assess(
        IReadOnlyList<VersionRepresentation> representations,
        ILookup<RepresentationId, string> localPaths,
        ILookup<RepresentationId, StorageReplica> replicas,
        IReadOnlySet<ReplicaId> activeReplicas,
        MaterializationPolicyProjectionResult policy)
    {
        if (representations.Count == 0)
            return (HistoryPresentationReadiness.MetadataOnly, MaterializationFidelity.Unknown, null, null);
        var ordered = representations
            .OrderBy(item => item.Fidelity == MaterializationFidelity.Exact ? 0 : 1)
            .ThenBy(item => item.RepresentationId.ToString(), StringComparer.Ordinal)
            .ToArray();
        foreach (var representation in ordered)
        {
            var localPath = localPaths[representation.RepresentationId].FirstOrDefault(path => File.Exists(path) || Directory.Exists(path));
            if (localPath is not null)
                return (HistoryPresentationReadiness.Ready, Fidelity(representation), representation, localPath);
        }
        foreach (var representation in ordered)
        {
            if (replicas[representation.RepresentationId].Any(replica => activeReplicas.Contains(replica.ReplicaId)))
                return (HistoryPresentationReadiness.PreparationRequired, Fidelity(representation), representation, null);
        }
        var fallback = ordered[0];
        if (ordered.Any(item => item.Kind == RepresentationKind.PluginArtifact))
            return (HistoryPresentationReadiness.PluginOrCredentialRequired, MaterializationFidelity.Unknown, fallback, null);
        if (policy.HasExplicitPolicy && policy.EffectiveState == MaterializationPolicyState.Released)
            return (HistoryPresentationReadiness.PayloadReleased, MaterializationFidelity.Unknown, fallback, null);
        return (HistoryPresentationReadiness.Unavailable,
            representations.Any(item => item.Fidelity == MaterializationFidelity.Partial)
                ? MaterializationFidelity.Partial : MaterializationFidelity.Unknown,
            fallback,
            null);
    }

    private static MaterializationFidelity Fidelity(VersionRepresentation representation)
        => representation.Fidelity;
}
