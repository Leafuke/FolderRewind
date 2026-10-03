using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

public sealed class HistoryChainRewriteRetentionService(HistoryRuntime history, RepresentationRuntime engine,
    IHistoryChainRewriteArchiveBackend archive, HistoryRetentionPlanner retention)
{
    public async Task<HistoryChainRewriteResult> ExecuteAsync(int keepCount, int maximumDeltaDepth,
        CancellationToken token = default)
    {
        if (keepCount <= 0) return new(false, false, 0, 0, 0, string.Empty);
        HistoryChainRewritePlan plan;
        await using (var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false))
        {
            var roots = await retention.PlanAsync(new(keepCount, HistoryRetentionOperationRoots.Empty, true), token).ConfigureAwait(false);
            if (!roots.CanExecute) return new(false, false, 0, 0, 0, string.Join(" ", roots.Blockers));
            var graph = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
            var versions = (await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false)).ToDictionary(v => v.VersionId);
            var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value!;
            var protectedVersions = roots.ProtectedVersions.Select(v => v.VersionId).ToHashSet();
            var protectedIds = roots.ProtectedOperationRepresentations.ToHashSet();
            protectedIds.UnionWith(history.MergeSessions.ProtectedRepresentations(graph.Values.ToArray()));
            var targets = catalog.Entries.Where(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                && !protectedIds.Contains(e.RepresentationId) && graph.TryGetValue(e.RepresentationId, out var r)
                && !protectedVersions.Contains(r.VersionId) && r.Fidelity == MaterializationFidelity.Exact
                && r.Kind is RepresentationKind.CoreFull or RepresentationKind.CoreRolling or RepresentationKind.CoreSmartDelta
                && versions[r.VersionId].BoundaryConfidence == HistoricalBoundaryConfidence.Known).ToArray();
            if (targets.Length == 0) return new(false, false, 0, 0, 0, string.Empty);
            var request = new HistoryChainRewriteRequest(HistoryChainRewriteOrigin.Retention,
                [.. targets.Select(e => e.LocalReplicaId)], [.. targets.Select(e => graph[e.RepresentationId].VersionId).Distinct()],
                [.. protectedVersions], maximumDeltaDepth);
            plan = await new HistoryChainRewritePlanner(history, engine).PlanInsideGateAsync(request, token).ConfigureAwait(false);
        }
        if (!plan.CanExecute) return new(false, false, 0, 0, 0, string.Join(" ", plan.Blockers));
        var executor = new HistoryChainRewriteExecutor(history, engine, archive);
        await using var prepared = await executor.PrepareAsync(plan, token: token).ConfigureAwait(false);
        return await executor.CommitAsync(prepared, token).ConfigureAwait(false);
    }
}
