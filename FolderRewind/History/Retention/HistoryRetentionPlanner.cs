using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

public sealed class HistoryRetentionPlanner
{
    private readonly HistoryRuntime _history;
    private readonly RepresentationRuntime _representations;
    private readonly Func<CancellationToken, Task<IRepresentationEnvironment>> _environmentFactory;
    private readonly IHistoryLocalPayloadStore _payloads;

    public HistoryRetentionPlanner(
        HistoryRuntime history,
        RepresentationRuntime representations,
        Func<CancellationToken, Task<IRepresentationEnvironment>> environmentFactory,
        IHistoryLocalPayloadStore payloads)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _representations = representations ?? throw new ArgumentNullException(nameof(representations));
        _environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
    }

    public Task<HistoryRetentionPlan> PlanAsync(
        HistoryRetentionRequest request,
        CancellationToken cancellationToken = default)
        => PlanAsync(request, ImmutableDictionary<VersionId, RepresentationId>.Empty, cancellationToken);

    internal async Task<HistoryRetentionPlan> PlanAsync(
        HistoryRetentionRequest request,
        IReadOnlyDictionary<VersionId, RepresentationId> preferredRepresentations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preferredRepresentations);
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);

        var checkpointsTask = _history.Query.GetAllCheckpointsAsync(cancellationToken);
        var runsTask = _history.Query.GetRunsAsync(cancellationToken);
        var branchUpdatesTask = _history.Query.GetAllBranchUpdatesAsync(cancellationToken);
        var annotationsTask = _history.Query.GetAllAnnotationUpdatesAsync(cancellationToken);
        var versionsTask = _history.Query.GetAllVersionsAsync(cancellationToken);
        var representationsTask = _history.Query.GetAllRepresentationsAsync(cancellationToken);
        await Task.WhenAll(
            checkpointsTask,
            runsTask,
            branchUpdatesTask,
            annotationsTask,
            versionsTask,
            representationsTask).ConfigureAwait(false);

        var checkpoints = checkpointsTask.Result;
        var checkpointMap = checkpoints.ToDictionary(item => item.CheckpointId);
        var runs = runsTask.Result;
        var branchUpdates = branchUpdatesTask.Result;
        var annotations = annotationsTask.Result;
        var versions = versionsTask.Result;
        var versionMap = versions.ToDictionary(item => item.VersionId);
        var allRepresentations = representationsTask.Result;
        var representationMap = allRepresentations.ToDictionary(item => item.RepresentationId);
        var catalogLoad = await _history.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var workspaceLoad = await _history.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        var catalog = catalogLoad.Value;
        if (catalogLoad.Status != DeviceLocalStateStatus.Valid)
            blockers.Add("Local Replica Catalog requires recovery before retention can plan deletion.");
        if (workspaceLoad.Status != DeviceLocalStateStatus.Valid)
            blockers.Add("Workspace must be valid before retention can prove its protection roots.");

        var policyTips = new Dictionary<VersionId, ImmutableArray<MaterializationPolicyUpdateId>>();
        var released = new HashSet<VersionId>();
        foreach (var version in versions)
        {
            var projection = await _history.Query.GetMaterializationPolicyProjectionAsync(
                version.VersionId,
                cancellationToken).ConfigureAwait(false);
            policyTips[version.VersionId] = projection.Tips
                .Select(item => item.UpdateId)
                .OrderBy(item => item.ToString(), StringComparer.Ordinal)
                .ToImmutableArray();
            if (projection.HasExplicitPolicy && projection.EffectiveState == MaterializationPolicyState.Released)
                released.Add(version.VersionId);
        }

        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        var inspectionCache = new Dictionary<LocalReplicaId, HistoryLocalPayloadInspection>();
        if (catalog is not null)
        {
            foreach (var entry in catalog.Entries)
            {
                inspectionCache[entry.LocalReplicaId] = await _payloads.InspectAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var selectionCache = new Dictionary<VersionId, Selection?>();
        async Task<Selection?> SelectVersionAsync(VersionId versionId)
        {
            if (selectionCache.TryGetValue(versionId, out var cached)) return cached;
            var assessment = await _representations.AssessVersionAsync(
                versionId,
                allRepresentations,
                environment,
                AssessmentDepth.Deep,
                request.RequiredFidelity,
                cancellationToken).ConfigureAwait(false);
            var ready = assessment.Candidates
                .Where(item => item.Readiness == HistoryReadiness.Ready
                    && Satisfies(item.Fidelity, request.RequiredFidelity))
                .Select(item =>
                {
                    var representation = representationMap[item.RepresentationId];
                    var closure = BuildDependencyFirstClosure(representation, representationMap);
                    return new Selection(
                        representation,
                        item.Fidelity,
                        closure,
                        EstimateClosureBytes(closure, catalog, inspectionCache));
                })
                .ToArray();
            Selection? selected = null;
            if (preferredRepresentations.TryGetValue(versionId, out var preferredId))
                selected = ready.SingleOrDefault(item => item.Root.RepresentationId == preferredId);
            selected ??= ready
                .OrderBy(item => item.EstimatedBytes ?? long.MaxValue)
                .ThenBy(item => item.Closure.Length)
                .ThenBy(item => item.Root.RepresentationId.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();
            selectionCache[versionId] = selected;
            return selected;
        }

        var checkpointReasons = new Dictionary<CheckpointId, HistoryProtectionReason>();
        var versionReasons = new Dictionary<VersionId, HistoryProtectionReason>();
        void ProtectVersion(VersionId versionId, HistoryProtectionReason reason)
            => versionReasons[versionId] = versionReasons.GetValueOrDefault(versionId) | reason;
        void ProtectCheckpoint(ConfigurationCheckpoint checkpoint, HistoryProtectionReason reason)
        {
            checkpointReasons[checkpoint.CheckpointId] = checkpointReasons.GetValueOrDefault(checkpoint.CheckpointId) | reason;
            foreach (var source in checkpoint.Sources)
            {
                if (source.VersionId is { } versionId) ProtectVersion(versionId, reason);
            }
        }

        var validBackupRuns = runs
            .Where(run => run.Outcome == BackupRunOutcome.Completed && run.ResultCheckpointId is not null)
            .ToDictionary(run => run.RunId);
        var vectors = new HashSet<string>(StringComparer.Ordinal);
        int retainedCheckpointCount = 0;
        foreach (var checkpoint in checkpoints
                     .Where(item => item.CreatedByRunId is { } runId
                         && validBackupRuns.TryGetValue(runId, out var run)
                         && run.ResultCheckpointId == item.CheckpointId
                         && item.IsComplete)
                     .OrderByDescending(item => item.CreatedAtUtc)
                     .ThenByDescending(item => item.CheckpointId.ToString(), StringComparer.Ordinal))
        {
            if (retainedCheckpointCount >= request.KeepCount) break;
            var vector = StateVector(checkpoint);
            if (vectors.Contains(vector)) continue;
            bool restorable = true;
            foreach (var source in checkpoint.Sources)
            {
                if (source.VersionId is not { } versionId
                    || await SelectVersionAsync(versionId).ConfigureAwait(false) is null)
                {
                    restorable = false;
                    break;
                }
            }
            if (!restorable) continue;
            vectors.Add(vector);
            ProtectCheckpoint(checkpoint, HistoryProtectionReason.KeepCount);
            retainedCheckpointCount++;
        }

        foreach (var branch in HistoryBranchProjection.Build(branchUpdates))
        {
            if (branch.Tips.Length == 1 && branch.Tips[0].IsDeleted) continue;
            foreach (var tip in branch.Tips)
            {
                if (tip.TargetCheckpointId is { } checkpointId
                    && checkpointMap.TryGetValue(checkpointId, out var checkpoint))
                {
                    ProtectCheckpoint(checkpoint, HistoryProtectionReason.BranchTip);
                }
            }
        }

        foreach (var group in annotations.GroupBy(item => item.Target))
        {
            if (!HistoryAnnotationProjection.Project(group.Key, group).IsPinned) continue;
            if (group.Key.Kind == HistoryAnnotationTargetKind.Version)
            {
                ProtectVersion(new VersionId(group.Key.TargetId), HistoryProtectionReason.Pin);
            }
            else if (group.Key.Kind == HistoryAnnotationTargetKind.Checkpoint
                && checkpointMap.TryGetValue(new CheckpointId(group.Key.TargetId), out var checkpoint))
            {
                ProtectCheckpoint(checkpoint, HistoryProtectionReason.Pin);
            }
        }

        if (workspaceLoad.Value is { } workspace)
        {
            foreach (var baseline in workspace.SourceBaselines)
            {
                if (baseline.BaseVersionId is { } versionId)
                    ProtectVersion(versionId, HistoryProtectionReason.Workspace);
            }
        }
        foreach (var versionId in request.ActiveOperations.VersionIds)
            ProtectVersion(versionId, HistoryProtectionReason.ActiveOperation);

        var closures = new List<HistoryRepresentationClosure>();
        var selections = new Dictionary<VersionId, Selection>();
        foreach (var protectedVersion in versionReasons.OrderBy(item => item.Key.ToString(), StringComparer.Ordinal))
        {
            if (!versionMap.ContainsKey(protectedVersion.Key))
            {
                blockers.Add($"Protected Version {protectedVersion.Key} is missing.");
                continue;
            }
            var selection = await SelectVersionAsync(protectedVersion.Key).ConfigureAwait(false);
            if (selection is null)
            {
                blockers.Add(
                    released.Contains(protectedVersion.Key)
                        ? $"Protected released Version {protectedVersion.Key} has no Ready local materialization; automatic rehydration is forbidden."
                        : $"Protected Version {protectedVersion.Key} has no Ready {request.RequiredFidelity} Representation.");
                continue;
            }
            selections.Add(protectedVersion.Key, selection);
            closures.Add(new HistoryRepresentationClosure(
                protectedVersion.Key,
                selection.Root.RepresentationId,
                selection.Closure.Select(item => item.RepresentationId).ToImmutableArray(),
                selection.Fidelity,
                released.Contains(protectedVersion.Key),
                selection.EstimatedBytes));
        }

        var operationClosure = new HashSet<RepresentationId>();
        foreach (var representationId in request.ActiveOperations.RepresentationIds)
        {
            if (!representationMap.TryGetValue(representationId, out var representation))
            {
                blockers.Add($"Active operation Representation {representationId} is missing.");
                continue;
            }
            operationClosure.UnionWith(BuildDependencyFirstClosure(representation, representationMap)
                .Select(item => item.RepresentationId));
        }

        var currentProtectedRepresentations = closures
            .SelectMany(item => item.DependencyFirstRepresentationIds)
            .Concat(operationClosure)
            .ToHashSet();
        var representationUseCounts = closures
            .SelectMany(item => item.DependencyFirstRepresentationIds.Distinct())
            .Concat(operationClosure)
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        var compactions = new List<HistoryCompactionPlan>();
        foreach (var pair in selections.OrderBy(item => item.Key.ToString(), StringComparer.Ordinal))
        {
            var selection = pair.Value;
            if (selection.Root.Kind != RepresentationKind.CoreSmartDelta
                || selection.Closure.Length < 2
                || released.Contains(pair.Key))
            {
                continue;
            }
            long reclaimable = selection.Closure
                .Where(item => representationUseCounts.GetValueOrDefault(item.RepresentationId) == 1)
                .SelectMany(item => catalog?.Entries.Where(entry => entry.RepresentationId == item.RepresentationId) ?? [])
                .Select(entry => inspectionCache.GetValueOrDefault(entry.LocalReplicaId))
                .Where(info => info?.PayloadFileExists == true)
                .Sum(info => info!.Size);
            if (reclaimable <= 0) continue;
            compactions.Add(new HistoryCompactionPlan(
                pair.Key,
                versionMap[pair.Key].SourceId,
                selection.Root.RepresentationId,
                RepresentationId.New(),
                selection.Closure.Select(item => item.RepresentationId).ToImmutableArray(),
                reclaimable));
        }

        var compactedVersions = compactions.Select(item => item.VersionId).ToHashSet();
        var hypotheticalProtected = selections
            .Where(item => !compactedVersions.Contains(item.Key))
            .SelectMany(item => item.Value.Closure)
            .Select(item => item.RepresentationId)
            .Concat(operationClosure)
            .ToHashSet();
        var deletions = new List<HistoryLocalPayloadDeletion>();
        if (blockers.Count == 0 && catalog is not null)
        {
            foreach (var entry in catalog.Entries
                         .Where(item => !hypotheticalProtected.Contains(item.RepresentationId))
                         .OrderBy(item => item.LocalReplicaId.ToString(), StringComparer.Ordinal))
            {
                var inspection = inspectionCache[entry.LocalReplicaId];
                if (!inspection.CanRemoveRegistration) continue;
                deletions.Add(new HistoryLocalPayloadDeletion(
                    entry.LocalReplicaId,
                    entry.RepresentationId,
                    inspection.ResolvedPath,
                    inspection.Size,
                    currentProtectedRepresentations.Contains(entry.RepresentationId)));
            }
        }

        var protectedArtifactRoots = currentProtectedRepresentations
            .Select(id => representationMap.GetValueOrDefault(id))
            .Where(item => item?.Kind == RepresentationKind.PluginArtifact)
            .Select(item => TryGetArtifactRoot(item!, out var root) ? root : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToImmutableArray();
        var protectedCheckpoints = checkpointReasons
            .OrderBy(item => item.Key.ToString(), StringComparer.Ordinal)
            .Select(item => new HistoryProtectedCheckpoint(item.Key, item.Value))
            .ToImmutableArray();
        var protectedVersions = versionReasons
            .OrderBy(item => item.Key.ToString(), StringComparer.Ordinal)
            .Select(item => new HistoryProtectedVersion(item.Key, item.Value))
            .ToImmutableArray();
        var fingerprint = Fingerprint(
            request,
            catalog?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision,
            workspaceLoad.Value,
            protectedCheckpoints,
            protectedVersions,
            closures,
            operationClosure,
            branchUpdates,
            annotations,
            policyTips,
            allRepresentations,
            catalog);
        return new HistoryRetentionPlan(
            HistoryTransactionId.New(),
            request,
            fingerprint,
            catalog?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision,
            protectedCheckpoints,
            protectedVersions,
            closures.OrderBy(item => item.VersionId.ToString(), StringComparer.Ordinal).ToImmutableArray(),
            operationClosure.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToImmutableArray(),
            protectedArtifactRoots,
            compactions.ToImmutableArray(),
            deletions.ToImmutableArray(),
            deletions.Sum(item => item.EstimatedBytes),
            blockers.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    private static string StateVector(ConfigurationCheckpoint checkpoint)
        => string.Join(
            "|",
            checkpoint.Sources
                .OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal)
                .Select(item => $"{item.SourceId}:{item.VersionId?.ToString() ?? "missing"}"));

    private static bool Satisfies(MaterializationFidelity actual, MaterializationFidelity required)
        => required == MaterializationFidelity.Exact
            ? actual == MaterializationFidelity.Exact
            : actual is MaterializationFidelity.Exact or MaterializationFidelity.Overlay;

    private static ImmutableArray<VersionRepresentation> BuildDependencyFirstClosure(
        VersionRepresentation root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
    {
        var visited = new HashSet<RepresentationId>();
        var result = new List<VersionRepresentation>();
        Visit(root);
        return result.ToImmutableArray();

        void Visit(VersionRepresentation representation)
        {
            if (!visited.Add(representation.RepresentationId)) return;
            foreach (var dependencyId in representation.DependencyRepresentationIds)
            {
                if (!graph.TryGetValue(dependencyId, out var dependency))
                    throw new InvalidOperationException($"Representation dependency {dependencyId} is missing.");
                Visit(dependency);
            }
            result.Add(representation);
        }
    }

    private static long? EstimateClosureBytes(
        IEnumerable<VersionRepresentation> closure,
        LocalReplicaCatalog? catalog,
        IReadOnlyDictionary<LocalReplicaId, HistoryLocalPayloadInspection> inspections)
    {
        long total = 0;
        foreach (var representation in closure)
        {
            var sizes = (catalog?.Entries ?? [])
                .Where(entry => entry.RepresentationId == representation.RepresentationId)
                .Select(entry => inspections.GetValueOrDefault(entry.LocalReplicaId))
                .Where(info => info?.PayloadFileExists == true)
                .Select(info => info!.Size)
                .ToArray();
            if (sizes.Length > 0)
            {
                total = checked(total + sizes.Min());
                continue;
            }
            if (representation.RepresentationSpecificMetadata.TryGetValue("estimatedBytes", out var text)
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var estimate)
                && estimate >= 0)
            {
                total = checked(total + estimate);
                continue;
            }
            return null;
        }
        return total;
    }

    private static bool TryGetArtifactRoot(VersionRepresentation representation, out Guid root)
    {
        root = Guid.Empty;
        return representation.RepresentationSpecificMetadata.TryGetValue(
                PluginArtifactRepresentationHandler.ArtifactRootIdMetadataKey,
                out var value)
            && Guid.TryParse(value, out root)
            && root != Guid.Empty;
    }

    private static string Fingerprint(
        HistoryRetentionRequest request,
        long catalogRevision,
        HistoryWorkspace? workspace,
        IEnumerable<HistoryProtectedCheckpoint> checkpoints,
        IEnumerable<HistoryProtectedVersion> versions,
        IEnumerable<HistoryRepresentationClosure> closures,
        IEnumerable<RepresentationId> operationRepresentations,
        IEnumerable<BranchUpdate> branchUpdates,
        IEnumerable<HistoryAnnotationUpdate> annotations,
        IReadOnlyDictionary<VersionId, ImmutableArray<MaterializationPolicyUpdateId>> policyTips,
        IEnumerable<VersionRepresentation> representations,
        LocalReplicaCatalog? catalog)
    {
        var text = new StringBuilder()
            .Append(request.KeepCount).Append('|').Append((int)request.RequiredFidelity).Append('|')
            .Append(catalogRevision).Append('|')
            .Append(workspace?.StateRevision.ToString(CultureInfo.InvariantCulture) ?? "missing").AppendLine();
        foreach (var item in checkpoints) text.Append("c:").Append(item.CheckpointId).Append(':').Append((int)item.Reasons).AppendLine();
        foreach (var item in versions) text.Append("v:").Append(item.VersionId).Append(':').Append((int)item.Reasons).AppendLine();
        foreach (var item in closures.OrderBy(item => item.VersionId.ToString(), StringComparer.Ordinal))
        {
            text.Append("x:").Append(item.VersionId).Append(':').Append(item.SelectedRepresentationId).Append(':')
                .AppendJoin(',', item.DependencyFirstRepresentationIds).AppendLine();
        }
        foreach (var id in operationRepresentations.OrderBy(item => item.ToString(), StringComparer.Ordinal))
            text.Append("o:").Append(id).AppendLine();
        var branchParentIds = branchUpdates.SelectMany(item => item.ParentUpdateIds).ToHashSet();
        foreach (var tip in branchUpdates.Where(item => !branchParentIds.Contains(item.UpdateId))
                     .OrderBy(item => item.UpdateId.ToString(), StringComparer.Ordinal))
            text.Append("b:").Append(tip.UpdateId).AppendLine();
        foreach (var group in annotations
                     .Where(item => item.AnnotationKind == HistoryAnnotationKind.Pin)
                     .GroupBy(item => item.Target)
                     .OrderBy(item => item.Key.Kind)
                     .ThenBy(item => item.Key.TargetId))
        {
            foreach (var tip in HistoryAnnotationProjection.FindTips(group)
                         .OrderBy(item => item.UpdateId.ToString(), StringComparer.Ordinal))
                text.Append("a:").Append(tip.UpdateId).AppendLine();
        }
        foreach (var pair in policyTips.OrderBy(item => item.Key.ToString(), StringComparer.Ordinal))
            text.Append("p:").Append(pair.Key).Append(':').AppendJoin(',', pair.Value).AppendLine();
        foreach (var representation in representations.OrderBy(item => item.RepresentationId.ToString(), StringComparer.Ordinal))
            text.Append("r:").Append(representation.RepresentationId).AppendLine();
        foreach (var entry in (catalog?.Entries ?? []).OrderBy(item => item.LocalReplicaId.ToString(), StringComparer.Ordinal))
            text.Append("l:").Append(entry.LocalReplicaId).Append(':').Append(entry.RepresentationId).Append(':')
                .Append(entry.Locator.Kind).Append(':').Append(entry.Locator.AbsolutePath).Append(':')
                .Append(entry.Locator.StableRootId).Append(':').Append(entry.Locator.RelativePath).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private sealed record Selection(
        VersionRepresentation Root,
        MaterializationFidelity Fidelity,
        ImmutableArray<VersionRepresentation> Closure,
        long? EstimatedBytes);
}
