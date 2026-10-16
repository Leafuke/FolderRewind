using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Retention;

public sealed record NativeArtifactCollectionResult(SourceId SourceId, int DeletedObjects, long ReclaimedBytes);

public static class NativeArtifactRetention
{
    public static async Task<IReadOnlyList<NativeArtifactCollectionResult>> CollectAsync(HistoryRuntime history,
        HistoryRetentionPlanner planner, NativeArtifactStore store, int keepCount, CancellationToken token)
    {
        await using var gate = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
        history.CloudJobs.RequireCleanupSafe();
        var catalog = await RequireCatalogAsync(history, token).ConfigureAwait(false);
        if (keepCount > 0)
        {
            var plan = await planner.PlanAsync(new(keepCount, HistoryRetentionOperationRoots.Empty, true), token).ConfigureAwait(false);
            if (!plan.CanExecute) throw new InvalidOperationException(string.Join("; ", plan.Blockers));
            var representations = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
            var protectedVersions = plan.ProtectedVersions.Select(v => v.VersionId).ToHashSet();
            var protectedRepresentations = plan.ProtectedClosures.SelectMany(c => c.DependencyFirstRepresentationIds)
                .Concat(plan.ProtectedOperationRepresentations).ToHashSet();
            var removable = representations.Where(NativePluginArtifactBackend.IsSupported)
                .Where(r => !protectedVersions.Contains(r.VersionId) && !protectedRepresentations.Contains(r.RepresentationId))
                .Select(r => r.RepresentationId).ToHashSet();
            if (catalog.Entries.Any(e => removable.Contains(e.RepresentationId)))
            {
                var updated = new LocalReplicaCatalog(history.ConfigId, checked(catalog.CatalogRevision + 1),
                    catalog.Entries.Where(e => !removable.Contains(e.RepresentationId)));
                var codec = new HistoryPackCodec();
                var marker = representations.First(r => catalog.Entries.Any(e => e.RepresentationId == r.RepresentationId && removable.Contains(r.RepresentationId)));
                var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(marker)]);
                var journal = HistoryTransactionJournal.Prepared(pack.TransactionId, pack.PackId,
                    [HistoryLocalStateJournalRecovery.CreateCatalogIntent(updated, catalog.CatalogRevision)]);
                token.ThrowIfCancellationRequested();
                await history.Repository.CommitAsync(pack, journal, CancellationToken.None).ConfigureAwait(false);
                await new HistoryLocalStateJournalRecovery(history.WorkspaceStore, history.LocalReplicaCatalogStore)
                    .ApplyCommittedStateAsync(journal, CancellationToken.None).ConfigureAwait(false);
                history.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
                await history.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.LocalStateChanged);
            }
        }
        return await CollectRegisteredAsync(history, store, token).ConfigureAwait(false);
    }

    // Caller owns the History mutation gate. Unregistered graph nodes are orphans regardless of KeepCount.
    internal static async Task<IReadOnlyList<NativeArtifactCollectionResult>> CollectRegisteredAsync(
        HistoryRuntime history, NativeArtifactStore store, CancellationToken token)
    {
        history.CloudJobs.RequireCleanupSafe();
        var catalog = await RequireCatalogAsync(history, token).ConfigureAwait(false);
        var representations = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var registered = catalog.Entries.Select(e => e.RepresentationId).ToHashSet();
        registered.UnionWith(history.CloudJobs.ProtectedRoots());
        registered.UnionWith(history.MergeSessions.ProtectedRepresentations(representations));
        var roots = new HashSet<ArtifactId>();
        foreach (var representation in representations.Where(r => registered.Contains(r.RepresentationId) && NativePluginArtifactBackend.IsSupported(r)))
        {
            if (!representation.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var text) || !Guid.TryParse(text, out var root))
                throw new InvalidDataException("Invalid Native History binding.");
            roots.Add(new(root));
        }
        var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
        var sizes = ledger.Artifacts.ToDictionary(a => a.ArtifactId,
            a => File.Exists(store.PayloadPath(a)) ? new FileInfo(store.PayloadPath(a)).Length : 0);
        var deleted = (await store.CollectAsync(roots, token).ConfigureAwait(false)).ToHashSet();
        return ledger.Artifacts.Where(a => deleted.Contains(a.ArtifactId) && !File.Exists(store.PayloadPath(a)))
            .GroupBy(a => a.FolderId).Select(g => new NativeArtifactCollectionResult(new(g.Key), g.Count(), g.Sum(a => sizes[a.ArtifactId]))).ToArray();
    }

    private static async Task<LocalReplicaCatalog> RequireCatalogAsync(HistoryRuntime history, CancellationToken token)
    {
        var catalog = await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        var workspace = await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false);
        if (catalog.Status != DeviceLocalStateStatus.Valid || workspace.Status != DeviceLocalStateStatus.Valid)
            throw new InvalidDataException("Local catalog and Workspace must be valid before Native GC.");
        return catalog.Value!;
    }
}
