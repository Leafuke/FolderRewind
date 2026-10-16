using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    // Caller holds the configuration operation lease across preparation, confirmation and commit.
    public static async Task<PreparedHistoryDeletion> PrepareVersionDeletionAsync(BackupConfig config,
        VersionId versionId, RepresentationId representationId, string localPath, bool hideRecord,
        bool releaseVersion, IProgress<HistoryChainRewriteProgress>? progress = null, CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var representation = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false))
            .Single(r => r.RepresentationId == representationId && r.VersionId == versionId);
        if (representation.Kind == RepresentationKind.PluginArtifact)
            return new(await new NativeArtifactVersionDeletion(runtime, Plugins.V3.NativeArtifactHostService.Store(config))
                .PrepareAsync(versionId, representationId, localPath, GetRewriteBackupRoot(config), hideRecord, releaseVersion, token).ConfigureAwait(false));
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value
            ?? throw new InvalidOperationException("Local Replica Catalog requires recovery.");
        var path = Path.GetFullPath(localPath);
        var targets = catalog.Entries.Where(e => e.RepresentationId == representationId
            && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, path)).Select(e => e.LocalReplicaId).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("The selected local backup is no longer registered.");
        var archive = new SevenZipHistoryArchiveBackend(config);
        var engine = RewriteEngine(archive, config, runtime);
        var request = new HistoryChainRewriteRequest(HistoryChainRewriteOrigin.Manual, [.. targets], [versionId], [],
            config.Archive.MaxSmartBackupsPerFull, hideRecord, releaseVersion, GetRewriteBackupRoot(config));
        progress?.Report(new("analyze", 0, 0));
        var plan = await new HistoryChainRewritePlanner(runtime, engine).PlanAsync(request, token).ConfigureAwait(false);
        return new(await new HistoryChainRewriteExecutor(runtime, engine, archive).PrepareAsync(plan, progress, token).ConfigureAwait(false));
    }

    public static async Task<HistoryChainRewriteResult> CommitVersionDeletionAsync(BackupConfig config,
        PreparedHistoryDeletion prepared, CancellationToken token = default)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(GetRewriteBackupRoot(config), prepared.BackupRoot))
            throw new InvalidOperationException("History changed after preparation; prepare the deletion again.");
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        if (prepared.Native is { } native)
            return await new NativeArtifactVersionDeletion(runtime, Plugins.V3.NativeArtifactHostService.Store(config)).CommitAsync(native, token).ConfigureAwait(false);
        var archive = new SevenZipHistoryArchiveBackend(config);
        return await new HistoryChainRewriteExecutor(runtime, RewriteEngine(archive, config, runtime), archive).CommitAsync(prepared.Core!, token).ConfigureAwait(false);
    }

    private static RepresentationRuntime RewriteEngine(SevenZipHistoryArchiveBackend archive, BackupConfig config, HistoryRuntime runtime)
        => HistoryRepresentationRuntimeFactory.Create(config, runtime, archive);

    private static string GetRewriteBackupRoot(BackupConfig config)
    {
        var root = HistoryRewriteStoragePaths.NormalizeBackupRoot(config.DestinationPath);
        var managed = HistoryRewriteStoragePaths.ManagedRoot(root, new HistoryConfigId(config.Id));
        foreach (var folder in config.SourceFolders)
            if (!BackupPathOverlapPolicy.Validate(folder.Path, managed).IsSafe)
                throw new InvalidOperationException("Backup rewrite storage overlaps a source directory.");
        return root;
    }
}
