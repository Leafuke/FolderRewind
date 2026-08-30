using FolderRewind.History.Domain;
using FolderRewind.History.Index;
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
    ImmutableArray<BranchId> BranchIds);

public sealed record CheckpointSummary(
    CheckpointId CheckpointId,
    DateTimeOffset CreatedAtUtc,
    bool IsComplete,
    bool IsPinned,
    ImmutableArray<CheckpointSource> Sources);

public sealed record RunSummary(
    RunId RunId,
    DateTimeOffset CompletedAtUtc,
    BackupRunOutcome Outcome,
    CheckpointId? ResultCheckpointId,
    bool IsImportant,
    string Comment,
    bool HasPartialCapture,
    ImmutableArray<BackupRunSourceResult> Sources,
    ImmutableArray<BranchId> BranchIds);

public sealed record BranchSummary(
    BranchId BranchId,
    string Name,
    ImmutableArray<BranchUpdate> Tips,
    bool IsUnborn,
    bool IsMultiTip,
    bool IsDeleted,
    bool HasNameCollision,
    bool IsActive,
    bool CanCheckout,
    bool CanRename,
    bool CanDelete);

public sealed record HistoryPresentationSnapshot(
    ImmutableArray<TimelineEntrySummary> Timeline,
    ImmutableArray<CheckpointSummary> Checkpoints,
    ImmutableArray<RunSummary> Runs,
    ImmutableArray<BranchSummary> Branches,
    BranchId? ActiveBranchId,
    BranchUpdateId? ActiveBranchUpdateId);

public sealed class HistoryPresentationQueryService
{
    private readonly HistoryRuntime _runtime;

    public HistoryPresentationQueryService(HistoryRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<HistoryPresentationSnapshot> QueryAsync(
        SourceId? sourceId = null,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
    {
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var versions = await _runtime.Query.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        var allRepresentations = await _runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = await _runtime.Query.GetAllCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        var runs = await _runtime.Query.GetRunsAsync(cancellationToken).ConfigureAwait(false);
        var annotations = await _runtime.Query.GetAllAnnotationUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var branchUpdates = await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var migrations = await _runtime.Query.GetMigrationRecordsAsync(cancellationToken).ConfigureAwait(false);
        var catalog = (await _runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var workspace = (await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
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
        var timeline = new List<TimelineEntrySummary>();
        foreach (var version in versions.Where(item => !supportIds.Contains(item.VersionId)
                     && (sourceId is null || item.SourceId == sourceId)))
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.VersionId.Value);
            var annotation = HistoryAnnotationProjection.Project(
                target,
                annotationGroups.GetValueOrDefault(target) ?? []);
            if (annotation.IsSuppressed && !includeSuppressed) continue;
            var policy = await _runtime.Query.GetMaterializationPolicyProjectionAsync(version.VersionId, cancellationToken)
                .ConfigureAwait(false);
            var reps = representationGroups.GetValueOrDefault(version.VersionId) ?? [];
            var state = await AssessAsync(reps, catalog, policy, cancellationToken).ConfigureAwait(false);
            var selected = state.Representation;
            var localPath = state.LocalPath;
            var fileName = selected?.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
                ?? selected?.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName")
                ?? (localPath is null ? null : Path.GetFileName(localPath));
            timeline.Add(new(
                version.VersionId, selected?.RepresentationId, version.SourceId, version.CreatedAtUtc,
                version.SourceDescriptorSnapshot.DisplayName, fileName, localPath,
                annotation.EffectiveComment ?? string.Empty, annotation.IsPinned, annotation.IsSuppressed,
                policy.HasExplicitPolicy && policy.EffectiveState == MaterializationPolicyState.Released,
                version.CaptureScope, state.Readiness, state.Fidelity, version.ParentVersionIds,
                children.GetValueOrDefault(version.VersionId, []),
                memberships.VersionBranches.GetValueOrDefault(version.VersionId, [])));
        }

        var checkpointSummaries = new List<CheckpointSummary>();
        foreach (var checkpoint in checkpoints)
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Checkpoint, checkpoint.CheckpointId.Value);
            var projection = HistoryAnnotationProjection.Project(target, annotationGroups.GetValueOrDefault(target) ?? []);
            checkpointSummaries.Add(new(checkpoint.CheckpointId, checkpoint.CreatedAtUtc, checkpoint.IsComplete,
                projection.IsPinned, checkpoint.Sources));
        }
        var runSummaries = runs.Select(run =>
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, run.RunId.Value);
            var projection = HistoryAnnotationProjection.Project(target, annotationGroups.GetValueOrDefault(target) ?? []);
            var hasPartialCapture = run.Outcome == BackupRunOutcome.Partial
                || run.SourceResults.Any(item => item.VersionId is { } versionId
                    && versionScopes.GetValueOrDefault(versionId) == CaptureScope.PartialSource);
            var branchIds = run.ResultCheckpointId is { } checkpointId
                ? memberships.CheckpointBranches.GetValueOrDefault(checkpointId, [])
                : [];
            return new RunSummary(run.RunId, run.CompletedAtUtc, run.Outcome, run.ResultCheckpointId,
                projection.IsRunImportant, projection.EffectiveComment ?? string.Empty,
                hasPartialCapture, run.SourceResults, branchIds);
        }).ToImmutableArray();
        var branchSummaries = branchProjection.Branches.Where(branch => !branch.IsDeleted).Select(branch =>
        {
            var name = branch.Tips.Where(item => !item.IsDeleted).Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault()
                ?? branch.Tips.FirstOrDefault()?.Name ?? string.Empty;
            bool unborn = branch.Tips.All(item => item.IsUnborn);
            bool canMutate = !branch.IsMultiTip && !branch.IsDeleted;
            bool isActive = workspace?.ActiveBranchId == branch.BranchId;
            return new BranchSummary(branch.BranchId, name, branch.Tips, unborn, branch.IsMultiTip,
                branch.IsDeleted, branch.HasNameCollision,
                isActive,
                !branch.IsDeleted && !unborn && branch.Tips.Any(item => item.TargetCheckpointId is not null),
                canMutate, canMutate && !isActive);
        }).OrderByDescending(branch => branch.IsActive)
            .ThenBy(branch => branch.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(branch => branch.BranchId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            timeline.OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.VersionId.ToString(), StringComparer.Ordinal).ToImmutableArray(),
            checkpointSummaries.OrderByDescending(item => item.CreatedAtUtc).ToImmutableArray(),
            runSummaries.OrderByDescending(item => item.CompletedAtUtc).ToImmutableArray(),
            branchSummaries,
            workspace?.ActiveBranchId,
            workspace?.ActiveBranchUpdateId);
    }

    private async Task<(
        HistoryPresentationReadiness Readiness,
        MaterializationFidelity Fidelity,
        VersionRepresentation? Representation,
        string? LocalPath)> AssessAsync(
        IReadOnlyList<VersionRepresentation> representations,
        LocalReplicaCatalog? catalog,
        MaterializationPolicyProjectionResult policy,
        CancellationToken cancellationToken)
    {
        if (representations.Count == 0)
            return (HistoryPresentationReadiness.MetadataOnly, MaterializationFidelity.Unknown, null, null);
        var ordered = representations
            .OrderBy(item => item.RestoreStrategy == RestoreStrategy.Exact ? 0 : 1)
            .ThenBy(item => item.RepresentationId.ToString(), StringComparer.Ordinal)
            .ToArray();
        foreach (var representation in ordered)
        {
            var localPath = catalog?.Entries.Where(item => item.RepresentationId == representation.RepresentationId)
                .Select(item => item.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath ? item.Locator.AbsolutePath : null)
                .FirstOrDefault(path => path is not null && (File.Exists(path) || Directory.Exists(path)));
            if (localPath is not null)
                return (HistoryPresentationReadiness.Ready, Fidelity(representation), representation, localPath);
        }
        foreach (var representation in ordered)
        {
            var replicas = await _runtime.Query.GetStorageReplicasAsync(representation.RepresentationId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var replica in replicas)
            {
                var lifecycle = await _runtime.Query.GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, cancellationToken)
                    .ConfigureAwait(false);
                var parentIds = lifecycle.SelectMany(item => item.ParentUpdateIds).ToHashSet();
                if (lifecycle.Where(item => !parentIds.Contains(item.UpdateId)).Any(item => item.State == ReplicaLifecycleState.Active))
                    return (HistoryPresentationReadiness.PreparationRequired, Fidelity(representation), representation, null);
            }
        }
        var fallback = ordered[0];
        if (ordered.Any(item => item.Kind == RepresentationKind.PluginArtifact))
            return (HistoryPresentationReadiness.PluginOrCredentialRequired, MaterializationFidelity.Unknown, fallback, null);
        if (policy.HasExplicitPolicy && policy.EffectiveState == MaterializationPolicyState.Released)
            return (HistoryPresentationReadiness.PayloadReleased, MaterializationFidelity.Unknown, fallback, null);
        return (HistoryPresentationReadiness.Unavailable,
            representations.Any(item => item.RestoreStrategy == RestoreStrategy.Overlay)
                ? MaterializationFidelity.Overlay : MaterializationFidelity.Unknown,
            fallback,
            null);
    }

    private static MaterializationFidelity Fidelity(VersionRepresentation representation)
        => representation.RestoreStrategy == RestoreStrategy.Overlay
            ? MaterializationFidelity.Overlay : MaterializationFidelity.Exact;
}
