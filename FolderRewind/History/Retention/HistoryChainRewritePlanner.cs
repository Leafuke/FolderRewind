using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

public sealed class HistoryChainRewritePlanner(HistoryRuntime history, RepresentationRuntime engine)
{
    public async Task<HistoryChainRewritePlan> PlanAsync(HistoryChainRewriteRequest request,
        CancellationToken token = default)
    {
        await using var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
        return await PlanInsideGateAsync(request, token).ConfigureAwait(false);
    }

    internal async Task<HistoryChainRewritePlan> PlanInsideGateAsync(HistoryChainRewriteRequest request,
        CancellationToken token)
    {
        request = request with { BackupRoot = HistoryRewriteStoragePaths.NormalizeBackupRoot(request.BackupRoot) };
        HistoryRewriteStoragePaths.RequireUnlinkedAncestors(HistoryRewriteStoragePaths.ManagedRoot(request.BackupRoot, history.ConfigId));
        await history.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        var load = await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        if (load.Status != DeviceLocalStateStatus.Valid || load.Value is null)
            throw new InvalidOperationException("Local Replica Catalog requires recovery.");
        var catalog = load.Value;
        var representations = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToImmutableArray();
        var graph = representations.ToDictionary(r => r.RepresentationId);
        var versions = (await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false)).ToDictionary(v => v.VersionId);
        var targets = request.TargetReplicaIds.ToHashSet();
        var removedVersions = request.TargetVersionIds.ToHashSet();
        var blockers = new List<string>();
        if (!Enum.IsDefined(request.Origin)) blockers.Add("Unknown chain rewrite operation.");
        if (targets.Count == 0 || removedVersions.Count == 0) blockers.Add("Select at least one local backup to delete.");
        if (!targets.IsSubsetOf(catalog.Entries.Select(e => e.LocalReplicaId).ToHashSet()))
            blockers.Add("A selected local backup is no longer registered.");
        if (request.MaximumDeltaDepth < 0) blockers.Add("Invalid maximum delta depth.");
        foreach (var entry in catalog.Entries.Where(e => targets.Contains(e.LocalReplicaId)))
        {
            if (entry.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath || Directory.Exists(entry.Locator.AbsolutePath))
                blockers.Add("Only controlled archive files can be deleted.");
            if (!graph.TryGetValue(entry.RepresentationId, out var r) || !Supported(r))
                blockers.Add("This archive format or historical boundary does not support safe chain rewriting.");
            else if (!removedVersions.Contains(r.VersionId))
                blockers.Add("The selected archive does not belong to a deletion target.");
        }
        if (!removedVersions.SetEquals(catalog.Entries.Where(e => targets.Contains(e.LocalReplicaId))
                .Select(e => graph.TryGetValue(e.RepresentationId, out var r) ? r.VersionId : default)))
            blockers.Add("Deletion versions must match the selected local archives.");
        var mergeProtected = history.MergeSessions.ProtectedRepresentations(representations);
        if (catalog.Entries.Any(e => targets.Contains(e.LocalReplicaId) && mergeProtected.Contains(e.RepresentationId)))
            blockers.Add("Representation is protected by an unfinished Merge Session.");
        if (request.Origin == HistoryChainRewriteOrigin.Manual)
            foreach (var id in removedVersions)
                try { await history.MaterializationPolicies.EnsureCanReleaseAsync(id, token).ConfigureAwait(false); }
                catch (MaterializationPolicyCommandException ex) { blockers.Add(ex.Message); }

        var before = new RepresentationEnvironment(catalog.Entries, [], []);
        var after = new RepresentationEnvironment(catalog.Entries.Where(e => !targets.Contains(e.LocalReplicaId)), [], []);
        var targetRepresentations = catalog.Entries.Where(e => targets.Contains(e.LocalReplicaId)).Select(e => e.RepresentationId).ToHashSet();
        var affected = representations.Where(r => Closure(r.RepresentationId, graph).Any(targetRepresentations.Contains))
            .Select(r => r.VersionId).ToHashSet();
        var protectedVersions = request.Origin == HistoryChainRewriteOrigin.Manual
            ? affected.Where(v => !removedVersions.Contains(v)).ToHashSet()
            : request.RetainedVersionIds.ToHashSet();
        var steps = new List<HistoryChainRewriteStep>();
        var planned = new HashSet<RepresentationId>();
        var validWithoutTargets = new Dictionary<RepresentationId, bool>();
        async Task<bool> UsableAsync(RepresentationId id)
        {
            if (validWithoutTargets.TryGetValue(id, out var usable)) return usable;
            var assessment = await engine.AssessVersionAsync(graph[id].VersionId, representations, after,
                AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
            usable = assessment.Candidates.Any(a => a.RepresentationId == id && a.Readiness == HistoryReadiness.Ready
                && a.Fidelity == MaterializationFidelity.Exact);
            validWithoutTargets[id] = usable;
            return usable;
        }
        async Task PlanRepresentationAsync(RepresentationId id)
        {
            if (planned.Contains(id) || await UsableAsync(id).ConfigureAwait(false)) return;
            var original = graph[id];
            if (!Supported(original)) throw new InvalidOperationException("A dependent version uses an unsupported archive or historical boundary.");
            if (mergeProtected.Contains(id)) throw new InvalidOperationException("Representation is protected by an unfinished Merge Session.");
            var alternatives = await engine.AssessVersionAsync(original.VersionId, representations, after,
                AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
            if (alternatives.Readiness == HistoryReadiness.Ready && alternatives.Selected is { } alternate)
            {
                planned.Add(id);
                steps.Add(new(original, alternate.RepresentationId, null, false, alternate.RepresentationId));
                return;
            }
            RepresentationId? parent = original.DependencyRepresentationIds.IsEmpty ? null : original.DependencyRepresentationIds[0];
            var skipped = false;
            while (parent is { } p && removedVersions.Contains(graph[p].VersionId))
            {
                skipped = true;
                var removed = graph[p];
                if (!Supported(removed)) throw new InvalidOperationException("A removed dependency cannot be safely rewritten.");
                parent = removed.DependencyRepresentationIds.IsEmpty ? null : removed.DependencyRepresentationIds[0];
            }
            if (parent is { } kept) await PlanRepresentationAsync(kept).ConfigureAwait(false);
            planned.Add(id);
            steps.Add(new(original, RepresentationId.New(), parent,
                !skipped && parent is not null && !targetRepresentations.Contains(id)));
        }
        foreach (var version in protectedVersions.OrderBy(v => v.ToString(), StringComparer.Ordinal))
        {
            var retained = await engine.AssessVersionAsync(version, representations, after, AssessmentDepth.Deep,
                MaterializationFidelity.Exact, token).ConfigureAwait(false);
            if (retained.Readiness == HistoryReadiness.Ready) continue;
            var old = await engine.AssessVersionAsync(version, representations, before, AssessmentDepth.Deep,
                MaterializationFidelity.Exact, token).ConfigureAwait(false);
            if (old.Readiness != HistoryReadiness.Ready || old.Selected is null)
            {
                blockers.Add("A dependent version cannot be restored exactly. Download missing archives or repair the chain first.");
                continue;
            }
            try { await PlanRepresentationAsync(old.Selected.RepresentationId).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { blockers.Add(ex.Message); }
        }
        return new(HistoryTransactionId.New(), request, await FingerprintAsync(history, token).ConfigureAwait(false),
            catalog, representations, [.. protectedVersions.OrderBy(v => v.ToString(), StringComparer.Ordinal)],
            [.. steps], [.. blockers.Distinct(StringComparer.Ordinal)]);

        bool Supported(VersionRepresentation r) => r.Fidelity == MaterializationFidelity.Exact
            && r.Kind is RepresentationKind.CoreFull or RepresentationKind.CoreRolling or RepresentationKind.CoreSmartDelta
            && r.DependencyRepresentationIds.Length <= 1
            && versions.TryGetValue(r.VersionId, out var version) && version.BoundaryConfidence == HistoricalBoundaryConfidence.Known;
    }

    internal static HashSet<RepresentationId> Closure(RepresentationId id, IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
    {
        var result = new HashSet<RepresentationId>();
        var visiting = new HashSet<RepresentationId>();
        void Visit(RepresentationId current)
        {
            if (visiting.Contains(current)) throw new InvalidOperationException("Cyclic archive dependency.");
            if (!result.Add(current)) return;
            if (!graph.TryGetValue(current, out var r)) throw new InvalidOperationException("An archive dependency is missing.");
            visiting.Add(current);
            foreach (var dependency in r.DependencyRepresentationIds) Visit(dependency);
            visiting.Remove(current);
        }
        Visit(id);
        return result;
    }

    public static async Task<string> FingerprintAsync(HistoryRuntime history, CancellationToken token = default)
    {
        var packs = await history.Repository.ReadAllPacksAsync(token).ConfigureAwait(false);
        var catalog = await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        var workspace = await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false);
        var representations = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var text = string.Join("|", packs.Select(p => p.Pack.PackId.ToString()).Order(StringComparer.Ordinal))
            + JsonSerializer.Serialize(catalog.Value) + JsonSerializer.Serialize(workspace.Value)
            + string.Join("|", history.MergeSessions.ProtectedRepresentations(representations).Select(r => r.ToString()).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
