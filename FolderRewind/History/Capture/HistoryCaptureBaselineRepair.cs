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
        if (catalog is null || !File.Exists(baseline.PayloadPath)
            || !graph.TryGetValue(baseline.BaseRepresentationId, out var root) || root.VersionId != baseline.BaseVersionId)
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
        if (state?.BaseVersionId is not { } versionId || state.Relation == WorkspaceBaselineRelation.Unknown
            || state.RequiresFullCapture) return null;
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var environment = new RepresentationEnvironment(catalog?.Entries ?? [], [], []);
        using var verification = new ArchiveRestoreVerificationContext(Models.RestoreArchivePreflightMode.Strict);
        var assessment = await engine.AssessVersionAsync(versionId, graph, environment, AssessmentDepth.Deep,
            MaterializationFidelity.Exact, token, verification).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected?.SelectedLocalPath is not { } path) return null;
        var representation = graph.Single(r => r.RepresentationId == assessment.Selected.RepresentationId);
        var version = await history.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
        if (version is null || version.BoundaryConfidence != HistoricalBoundaryConfidence.Known) return null;
        using var held = await engine.LockExactVersionAsync(versionId, graph, environment, token, verification).ConfigureAwait(false);
        var staging = Path.Combine(history.Repository.Paths.TransactionsRoot, "baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            await engine.MaterializeAsync(representation.RepresentationId, graph, environment, MaterializationFidelity.Exact, staging, token, held.VerificationContext).ConfigureAwait(false);
            var existing = await history.CaptureBaselines.LoadAsync(source, token).ConfigureAwait(false);
            var files = ReadMetadata(staging, token);
            await history.CaptureBaselines.SaveAsync(source, new(existing?.Revision ?? -1, path,
                HistoryChainRewriteExecutor.DeltaDepth(representation.RepresentationId, graph.ToDictionary(r => r.RepresentationId)),
                files, version.EffectiveSourceBoundaryFingerprint), versionId, representation, token).ConfigureAwait(false);
            return await history.CaptureBaselines.LoadAsync(source, token).ConfigureAwait(false);
        }
        finally { new HistoryChainRewriteJournalStore(history).CleanupOwnedTree(staging); }
    }

    // Core baseline stores statistics, not a content witness. Preserve structural checks without hashing discarded bytes.
    internal static ImmutableSortedDictionary<string, SourceCaptureFileState> ReadMetadata(string root, CancellationToken token)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Baseline root cannot be a filesystem alias.");
        var files = ImmutableSortedDictionary.CreateBuilder<string, SourceCaptureFileState>(StringComparer.Ordinal);
        var directories = SourceDirectorySnapshot.Read(root, _ => true, [], token, enforceLimits: false);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Baseline input contains a filesystem alias.");
            var relative = Plugin.Runtime.Artifacts.ArtifactPathRules.NormalizeRelativePath(Path.GetRelativePath(root, path).Replace('\\', '/'));
            files.Add(relative, new(info.Length, info.LastWriteTimeUtc));
        }
        SourceDirectorySnapshot.Validate(directories, files.Keys, enforceLimits: false);
        Services.HistoryOperationDiagnostics.Current?.Count("baseline-metadata-files", files.Count);
        return files.ToImmutable();
    }
}
