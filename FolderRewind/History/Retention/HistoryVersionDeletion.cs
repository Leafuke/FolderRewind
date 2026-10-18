using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

/// <summary>One confirmation surface; each storage kind retains its own transaction strategy.</summary>
public sealed class PreparedHistoryDeletion : IAsyncDisposable
{
    public PreparedHistoryDeletion(PreparedHistoryChainRewrite core) { Core = core; }
    public PreparedHistoryDeletion(PreparedNativeArtifactDeletion native) { Native = native; }
    public PreparedHistoryChainRewrite? Core { get; }
    public PreparedNativeArtifactDeletion? Native { get; }
    public bool IsNative => Native is not null;
    public string? BackupRoot => Native?.BackupRoot ?? Core!.Plan.Request.BackupRoot;
    public int TargetReplicaCount => Native?.TargetReplicaIds.Length ?? Core!.Plan.Request.TargetReplicaIds.Length;
    public int RewrittenVersions => Core?.Mappings.Length ?? 0;
    public long CreatedBytes => Core?.CreatedBytes ?? 0;
    public long ReclaimedBytes => Core?.ReclaimedBytes ?? 0;
    public long NetReleasedBytes => ReclaimedBytes - CreatedBytes;
    public ValueTask DisposeAsync()
    {
        if (Native is not null) Native.Disposed = true;
        return Core?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

public sealed record PreparedNativeArtifactDeletion(VersionRepresentation Representation,
    ImmutableArray<LocalReplicaId> TargetReplicaIds, LocalReplicaCatalog Catalog, string StateFingerprint,
    string BackupRoot, bool HideRecord, bool ReleaseVersion)
{
    internal bool CommitStarted { get; set; }
    internal bool Disposed { get; set; }
}

/// <summary>Deletion changes logical ownership first. Physical DAG collection is repeatable and separate.</summary>
public sealed class NativeArtifactVersionDeletion(HistoryRuntime history, NativeArtifactStore store, Action<string>? checkpoint = null)
{
    public async Task<PreparedNativeArtifactDeletion> PrepareAsync(VersionId versionId, RepresentationId representationId,
        string localPath, string backupRoot, bool hideRecord, bool releaseVersion, CancellationToken token)
    {
        await using var gate = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
        history.CloudJobs.RequireCleanupSafe();
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var representation = graph.Single(r => r.RepresentationId == representationId && r.VersionId == versionId);
        if (!NativePluginArtifactBackend.IsSupported(representation))
            throw new InvalidOperationException("[history.unsupported-experimental-format] Native deletion requires evidence v2.");
        await RequireUnprotectedAsync(representation, graph, token).ConfigureAwait(false);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value
            ?? throw new InvalidDataException("Local Replica Catalog requires recovery.");
        var path = Path.GetFullPath(localPath);
        var targets = catalog.Entries.Where(e => e.RepresentationId == representationId
            && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, path)).Select(e => e.LocalReplicaId).ToImmutableArray();
        if (targets.IsEmpty) throw new InvalidOperationException("The selected local backup is no longer registered.");
        return new(representation, targets, catalog,
            await HistoryChainRewritePlanner.FingerprintAsync(history, token).ConfigureAwait(false), backupRoot, hideRecord, releaseVersion);
    }

    public async Task<HistoryChainRewriteResult> CommitAsync(PreparedNativeArtifactDeletion prepared, CancellationToken token)
    {
        await using var gate = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
        history.CloudJobs.RequireCleanupSafe();
        if (prepared.Disposed || prepared.CommitStarted || prepared.StateFingerprint != await HistoryChainRewritePlanner.FingerprintAsync(history, token).ConfigureAwait(false))
            throw new InvalidOperationException("History changed after preparation; prepare the deletion again.");
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        await RequireUnprotectedAsync(prepared.Representation, graph, token).ConfigureAwait(false);
        var version = prepared.Representation.VersionId;
        var facts = new List<object>();
        if (prepared.ReleaseVersion)
        {
            var tips = await history.Query.GetMaterializationPolicyTipsAsync(version, token).ConfigureAwait(false);
            facts.Add(new MaterializationPolicyUpdate(MaterializationPolicyUpdateId.New(), version, tips.Select(t => t.UpdateId),
                MaterializationPolicyState.Released, DateTimeOffset.UtcNow, "Native local closure deletion"));
        }
        if (prepared.HideRecord)
        {
            var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.Value);
            var updates = await history.Query.GetAnnotationUpdatesAsync(target, HistoryAnnotationKind.Suppression, token).ConfigureAwait(false);
            facts.Add(new HistoryAnnotationUpdate(AnnotationUpdateId.New(), target, HistoryAnnotationKind.Suppression,
                HistoryAnnotationProjection.FindTips(updates).Select(t => t.UpdateId), "true", DateTimeOffset.UtcNow));
        }
        if (facts.Count == 0) facts.Add(prepared.Representation);
        var codec = new HistoryPackCodec();
        var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, facts.Select(f => codec.CreateObject(f)));
        var catalog = new LocalReplicaCatalog(history.ConfigId, checked(prepared.Catalog.CatalogRevision + 1),
            prepared.Catalog.Entries.Where(e => !prepared.TargetReplicaIds.Contains(e.LocalReplicaId)));
        var journal = HistoryTransactionJournal.Prepared(pack.TransactionId, pack.PackId,
            [HistoryLocalStateJournalRecovery.CreateCatalogIntent(catalog, prepared.Catalog.CatalogRevision)]);
        token.ThrowIfCancellationRequested();
        prepared.CommitStarted = true;
        try
        {
            checkpoint?.Invoke("before-pack");
            await history.Repository.CommitAsync(pack, journal, CancellationToken.None).ConfigureAwait(false);
            checkpoint?.Invoke("pack-installed");
            await new HistoryLocalStateJournalRecovery(history.WorkspaceStore, history.LocalReplicaCatalogStore)
                .ApplyCommittedStateAsync(journal, CancellationToken.None).ConfigureAwait(false);
            checkpoint?.Invoke("catalog-applied");
            history.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
            await history.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            var collection = await NativeArtifactRetention.CollectRegisteredAsync(history, store, CancellationToken.None).ConfigureAwait(false);
            await history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.TransactionCommitted);
            history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.LocalStateChanged);
            return new(true, false, collection.Sum(r => r.ReclaimedBytes), 0, 0, string.Empty);
        }
        catch (Exception ex) when (File.Exists(history.Repository.Paths.GetPackPath(pack.PackId)))
        {
            return new(true, true, 0, 0, 0, "Native deletion is durable; recovery or collection is required: " + ex.Message)
                { RecoveryRequired = true, ReasonCode = "RecoveryRequired" };
        }
    }

    private async Task RequireUnprotectedAsync(VersionRepresentation representation, IReadOnlyList<VersionRepresentation> graph, CancellationToken token)
    {
        await history.MaterializationPolicies.EnsureCanReleaseAsync(representation.VersionId, token).ConfigureAwait(false);
        if (history.CloudJobs.ProtectedRoots().Contains(representation.RepresentationId)
            || history.MergeSessions.ProtectedRepresentations(graph).Contains(representation.RepresentationId))
            throw new InvalidOperationException("The selected local backup is required by an active operation.");
        if (graph.Any(r => r.RepresentationId != representation.RepresentationId && r.DependencyRepresentationIds.Contains(representation.RepresentationId)))
            throw new InvalidOperationException("The selected local backup is required by another Version Representation.");
    }
}
