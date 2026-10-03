using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Capture;

public static class HistoryCaptureBaselineRepair
{
    public static async Task<bool> IsLocallyAvailableAsync(HistoryRuntime history, SourceCaptureBaseline baseline, CancellationToken token)
    {
        var graph = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (catalog is null || !graph.TryGetValue(baseline.BaseRepresentationId, out var root) || root.VersionId != baseline.BaseVersionId)
            return false;
        if (!catalog.Entries.Any(e => e.RepresentationId == root.RepresentationId
                && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, baseline.PayloadPath))) return false;
        try
        {
            return HistoryChainRewritePlanner.Closure(root.RepresentationId, graph).All(id =>
                graph[id].Fidelity == MaterializationFidelity.Exact && catalog.Entries.Any(e => e.RepresentationId == id
                    && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && File.Exists(e.Locator.AbsolutePath)));
        }
        catch (InvalidOperationException) { return false; }
    }

    public static async Task<SourceCaptureBaseline?> RebuildAsync(HistoryRuntime history, RepresentationRuntime engine,
        SourceId source, CancellationToken token = default)
    {
        var workspace = (await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var state = workspace?.SourceBaselines.SingleOrDefault(s => s.SourceId == source);
        if (state?.BaseVersionId is not { } versionId || state.Relation == WorkspaceBaselineRelation.Unknown) return null;
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var environment = new RepresentationEnvironment(catalog?.Entries ?? [], [], []);
        var assessment = await engine.AssessVersionAsync(versionId, graph, environment, AssessmentDepth.Deep,
            MaterializationFidelity.Exact, token).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected?.SelectedLocalPath is not { } path) return null;
        var representation = graph.Single(r => r.RepresentationId == assessment.Selected.RepresentationId);
        var version = await history.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
        if (version is null || version.BoundaryConfidence != HistoricalBoundaryConfidence.Known) return null;
        using var held = await engine.LockExactVersionAsync(versionId, graph, environment, token).ConfigureAwait(false);
        var staging = Path.Combine(history.Repository.Paths.TransactionsRoot, "baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            await engine.MaterializeAsync(representation.RepresentationId, graph, environment, MaterializationFidelity.Exact, staging, token).ConfigureAwait(false);
            var tree = await HistoryRewriteTree.ReadAsync(staging, token).ConfigureAwait(false);
            var existing = await history.CaptureBaselines.LoadAsync(source, token).ConfigureAwait(false);
            var files = tree.Files.ToImmutableSortedDictionary(p => p.Key, p => new SourceCaptureFileState(p.Value.Length, p.Value.LastWriteUtc), StringComparer.Ordinal);
            await history.CaptureBaselines.SaveAsync(source, new(existing?.Revision ?? -1, path,
                HistoryChainRewriteExecutor.DeltaDepth(representation.RepresentationId, graph.ToDictionary(r => r.RepresentationId)),
                files, version.EffectiveSourceBoundaryFingerprint), versionId, representation, token).ConfigureAwait(false);
            return await history.CaptureBaselines.LoadAsync(source, token).ConfigureAwait(false);
        }
        finally { new HistoryChainRewriteJournalStore(history).CleanupOwnedTree(staging); }
    }
}
