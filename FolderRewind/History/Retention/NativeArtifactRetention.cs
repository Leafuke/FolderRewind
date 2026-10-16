using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Retention;

public static class NativeArtifactRetention
{
    public static async Task CollectAsync(HistoryRuntime history, HistoryRetentionPlanner planner, NativeArtifactStore store,
        int keepCount, CancellationToken token)
    {
        await using var gate = await history.MutationGate.EnterAsync(token);
        var plan = await planner.PlanAsync(new(keepCount, HistoryRetentionOperationRoots.Empty, true), token);
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join("; ", plan.Blockers));
        var representations = await history.Query.GetAllRepresentationsAsync(token);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token)).Value
            ?? throw new InvalidOperationException("Local catalog must be valid before Artifact GC.");
        var protectedVersions = plan.ProtectedVersions.Select(v => v.VersionId).ToHashSet();
        var protectedRepresentations = plan.ProtectedClosures.SelectMany(c => c.DependencyFirstRepresentationIds)
            .Concat(plan.ProtectedOperationRepresentations).ToHashSet();
        var localRepresentations = catalog.Entries.Select(e => e.RepresentationId).ToHashSet();
        var removable = new HashSet<RepresentationId>();
        var roots = new HashSet<ArtifactId>();
        foreach (var representation in representations.Where(r => r.Kind == RepresentationKind.PluginArtifact))
        {
            if (!representation.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var text)
                || !Guid.TryParse(text, out var root)) throw new InvalidOperationException("Invalid Artifact History binding.");
            bool coreRemains = representation.RepresentationSpecificMetadata.TryGetValue("pairedCoreRepresentationId", out var pair)
                && Guid.TryParse(pair, out var pairedId) && localRepresentations.Contains(new(pairedId));
            if (protectedVersions.Contains(representation.VersionId) || protectedRepresentations.Contains(representation.RepresentationId) || coreRemains)
                roots.Add(new(root));
            else removable.Add(representation.RepresentationId);
        }
        if (catalog.Entries.Any(e => removable.Contains(e.RepresentationId)))
        {
            var updated = new LocalReplicaCatalog(history.ConfigId, checked(catalog.CatalogRevision + 1),
                catalog.Entries.Where(e => !removable.Contains(e.RepresentationId)));
            await history.LocalReplicaCatalogStore.SaveAsync(updated, catalog.CatalogRevision, token);
        }
        // Native payloads are deleted only by the DAG collector under its shared lease gate.
        await store.CollectAsync(roots, token);
    }
}
