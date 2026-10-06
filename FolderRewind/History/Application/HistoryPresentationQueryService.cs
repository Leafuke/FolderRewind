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
    CheckpointId? CheckpointId = null, bool IsLocalPayloadMissing = false,
    HistoricalBoundaryConfidence BoundaryConfidence = HistoricalBoundaryConfidence.Known);

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

    public Task<HistoryPresentationSnapshot> QueryAsync(SourceId? sourceId = null, bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
        => Task.Run(() => QueryCoreAsync(sourceId, includeSuppressed, cancellationToken), cancellationToken);

    public Task<HistoryPresentationPage> QueryPageAsync(HistoryPageRequest request, CancellationToken token = default)
        => Task.Run(async () =>
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var revision = _runtime.ChangeFeed.CurrentSequence;
                var snapshot = await QueryCoreAsync(request.SourceId, false, token, request.ByRun, scoped: true).ConfigureAwait(false);
                if (revision != _runtime.ChangeFeed.CurrentSequence) continue;
                return HistoryPresentationPager.Page(snapshot, request, revision);
            }
        }, token);

    private async Task<HistoryPresentationSnapshot> QueryCoreAsync(
        SourceId? sourceId = null,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default, bool? byRun = null, bool scoped = false)
    {
        // Only standalone profiling callers request timing; normal UI queries do not.
        var timer = _measure is null ? null : Stopwatch.StartNew();
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        _measure?.Invoke("index-current", timer!.Elapsed);
        timer?.Restart();
        var versions = scoped && sourceId is { } sourceForversions
            ? await _runtime.Index.ReadSourceFactsAsync<SourceVersion>(sourceForversions, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        var allRepresentations = scoped && sourceId is { } sourceForallRepresentations
            ? await _runtime.Index.ReadSourceFactsAsync<VersionRepresentation>(sourceForallRepresentations, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = scoped && sourceId is { } sourceForcheckpoints
            ? await _runtime.Index.ReadSourceFactsAsync<SourceCheckpoint>(sourceForcheckpoints, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BackupRun> runs = byRun == false ? [] : await _runtime.Query.GetRunsAsync(cancellationToken).ConfigureAwait(false);
        var annotations = scoped && sourceId is { } sourceForannotations
            ? await _runtime.Index.ReadSourceFactsAsync<HistoryAnnotationUpdate>(sourceForannotations, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllAnnotationUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var branchUpdates = scoped && sourceId is { } sourceForbranchUpdates
            ? await _runtime.Index.ReadSourceFactsAsync<BranchUpdate>(sourceForbranchUpdates, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var migrations = await _runtime.Query.GetMigrationRecordsAsync(cancellationToken).ConfigureAwait(false);
        var policies = scoped && sourceId is { } sourceForpolicies
            ? await _runtime.Index.ReadSourceFactsAsync<MaterializationPolicyUpdate>(sourceForpolicies, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllMaterializationPolicyUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var replicas = scoped && sourceId is { } sourceForreplicas
            ? await _runtime.Index.ReadSourceFactsAsync<StorageReplica>(sourceForreplicas, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllStorageReplicasAsync(cancellationToken).ConfigureAwait(false);
        var lifecycle = scoped && sourceId is { } sourceForlifecycle
            ? await _runtime.Index.ReadSourceFactsAsync<ReplicaLifecycleUpdate>(sourceForlifecycle, cancellationToken).ConfigureAwait(false)
            : await _runtime.Query.GetAllReplicaLifecycleUpdatesAsync(cancellationToken).ConfigureAwait(false);
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
        var pathCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string path)
        {
            if (!pathCache.TryGetValue(path, out var exists)) pathCache[path] = exists = File.Exists(path) || Directory.Exists(path);
            return exists;
        }
        var availability = new Dictionary<(RepresentationId, bool), bool>();
        bool Available(VersionRepresentation representation, bool allowRemote, HashSet<RepresentationId> visiting)
        {
            var key = (representation.RepresentationId, allowRemote);
            if (availability.TryGetValue(key, out var cached)) return cached;
            if (!visiting.Add(representation.RepresentationId)) return false;
            var present = localPaths[representation.RepresentationId].Any(Exists)
                || (allowRemote && replicaGroups[representation.RepresentationId].Any(r => activeReplicas.Contains(r.ReplicaId)));
            var complete = present && representation.DependencyRepresentationIds.All(id =>
                representationMap.TryGetValue(id, out var dependency) && Available(dependency, allowRemote, visiting));
            visiting.Remove(representation.RepresentationId);
            return availability[key] = complete;
        }
        var timeline = new List<TimelineEntrySummary>();
        foreach (var checkpoint in checkpoints.Where(c => byRun != true && c.CreationKind != CheckpointCreationKind.SafetySnapshot
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
            var state = Assess(reps, localPaths, policy, Exists,
                (representation, remote) => Available(representation, remote, new HashSet<RepresentationId>()));
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
                admission.IsReady ? 1 : 0, checkpoint.CreationKind == CheckpointCreationKind.Merge ? SourceVersionCreationKind.Merge : version.CreationKind, checkpoint.CheckpointId,
                reps.SelectMany(rep => localPaths[rep.RepresentationId]).Any()
                && !reps.SelectMany(rep => localPaths[rep.RepresentationId]).Any(Exists), version.BoundaryConfidence));
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
        var branchMap = branchUpdates.ToDictionary(item => item.UpdateId);
        var runSummaries = runs.Select(run =>
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, run.RunId.Value);
            var projection = HistoryAnnotationProjection.Project(target, annotationGroups.GetValueOrDefault(target) ?? []);
            var hasPartialCapture = run.SourceResults.Any(item => item.VersionId is { } versionId
                    && versionScopes.GetValueOrDefault(versionId) == CaptureScope.PartialSource);
            var branchIds = run.SourceResults.Where(r => r.BranchUpdateId is not null)
                .Select(r => branchMap.GetValueOrDefault(r.BranchUpdateId!.Value)?.BranchId)
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
        MaterializationPolicyProjectionResult policy, Func<string, bool> exists,
        Func<VersionRepresentation, bool, bool> available)
    {
        if (representations.Count == 0)
            return (HistoryPresentationReadiness.MetadataOnly, MaterializationFidelity.Unknown, null, null);
        var ordered = representations
            .OrderBy(item => item.Fidelity == MaterializationFidelity.Exact ? 0 : 1)
            .ThenBy(item => item.RepresentationId.ToString(), StringComparer.Ordinal)
            .ToArray();
        foreach (var representation in ordered)
        {
            var localPath = localPaths[representation.RepresentationId].FirstOrDefault(exists);
            if (localPath is not null && available(representation, false))
                return (HistoryPresentationReadiness.Ready, Fidelity(representation), representation, localPath);
        }
        foreach (var representation in ordered)
        {
            if (available(representation, true))
                return (HistoryPresentationReadiness.PreparationRequired, Fidelity(representation), representation,
                    localPaths[representation.RepresentationId].FirstOrDefault(exists));
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

public sealed record HistoryPageCursor(long Revision, string QueryKey, DateTimeOffset Time, string Id);
public sealed record HistoryPageRequest(SourceId? SourceId, bool ByRun, bool Advanced, BranchId? BranchId,
    string Keyword, int PageSize = 100, HistoryPageCursor? Cursor = null,
    IReadOnlyDictionary<BackupRunOutcome, string>? OutcomeNames = null);
public sealed record HistoryPresentationPage(HistoryPresentationSnapshot Snapshot, HistoryPageCursor? Next,
    int TotalCount, int MissingCount, long Revision, bool CursorInvalidated);

public static class HistoryPresentationPager
{
    public static HistoryPresentationPage Page(HistoryPresentationSnapshot snapshot, HistoryPageRequest request, long revision)
    {
        var branch = Services.HistoryPresentationPolicy.ResolveBranch(request.Advanced
            ? Models.HistoryPresentationMode.Advanced : Models.HistoryPresentationMode.Normal, snapshot.Branches, request.BranchId);
        var needle = request.Keyword.Trim();
        var key = $"{request.SourceId}|{request.ByRun}|{request.Advanced}|{branch}|{needle}";
        var invalid = request.Cursor is { } previous && (previous.Revision != revision || previous.QueryKey != key);
        var cursor = invalid ? null : request.Cursor;
        bool Matches(string? value) => value?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
        bool After(DateTimeOffset time, string id) => cursor is null || time < cursor.Time
            || (time == cursor.Time && StringComparer.Ordinal.Compare(id, cursor.Id) < 0);
        var size = Math.Clamp(request.PageSize, 1, 100);
        if (request.ByRun)
        {
            var all = snapshot.Runs.Where(r => needle.Length == 0 || Matches(r.Comment)
                || (string.IsNullOrWhiteSpace(r.Comment) && Matches(request.OutcomeNames?.GetValueOrDefault(r.Outcome) ?? r.Outcome.ToString())))
                .OrderByDescending(r => r.CompletedAtUtc).ThenByDescending(r => r.RunId.ToString(), StringComparer.Ordinal).ToArray();
            var rows = all.Where(r => After(r.CompletedAtUtc, r.RunId.ToString())).Take(size + 1).ToArray();
            var page = rows.Take(size).ToImmutableArray();
            var next = rows.Length > size ? new HistoryPageCursor(revision, key, page[^1].CompletedAtUtc, page[^1].RunId.ToString()) : null;
            return new(snapshot with { Timeline = [], Runs = page }, next, all.Length, 0, revision, invalid);
        }
        string Id(TimelineEntrySummary item) => item.CheckpointId?.ToString() ?? item.VersionId.ToString();
        var names = snapshot.Branches.ToDictionary(b => b.BranchId, b => b.Name);
        var versions = snapshot.Timeline.Where(v => (branch is null || v.BranchIds.Contains(branch.Value))
            && (needle.Length == 0 || Matches(v.Comment) || Matches(string.IsNullOrWhiteSpace(v.Comment) ? v.DisplayName : v.Comment)
                || Matches(v.FileName ?? v.DisplayName) || Matches(string.Join(" · ", v.BranchIds.Select(id => names.GetValueOrDefault(id)).Where(n => !string.IsNullOrWhiteSpace(n))))))
            .OrderByDescending(v => v.CreatedAtUtc).ThenByDescending(Id, StringComparer.Ordinal).ToArray();
        var selected = versions.Where(v => After(v.CreatedAtUtc, Id(v))).Take(size + 1).ToArray();
        var entries = selected.Take(size).ToImmutableArray();
        var continuation = selected.Length > size ? new HistoryPageCursor(revision, key, entries[^1].CreatedAtUtc, Id(entries[^1])) : null;
        return new(snapshot with { Timeline = entries, Runs = [] }, continuation, versions.Length,
            versions.Count(v => v.IsLocalPayloadMissing), revision, invalid);
    }
}
