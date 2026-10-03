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
    public static async Task<PreparedHistoryChainRewrite> PrepareVersionDeletionAsync(BackupConfig config,
        VersionId versionId, RepresentationId representationId, string localPath, bool hideRecord,
        bool releaseVersion, IProgress<HistoryChainRewriteProgress>? progress = null, CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value
            ?? throw new InvalidOperationException("Local Replica Catalog requires recovery.");
        var path = Path.GetFullPath(localPath);
        var targets = catalog.Entries.Where(e => e.RepresentationId == representationId
            && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, path)).Select(e => e.LocalReplicaId).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("The selected local backup is no longer registered.");
        var archive = new SevenZipHistoryArchiveBackend(config);
        var engine = RewriteEngine(archive);
        var request = new HistoryChainRewriteRequest(HistoryChainRewriteOrigin.Manual, [.. targets], [versionId], [],
            config.Archive.MaxSmartBackupsPerFull, hideRecord, releaseVersion);
        progress?.Report(new("analyze", 0, 0));
        var plan = await new HistoryChainRewritePlanner(runtime, engine).PlanAsync(request, token).ConfigureAwait(false);
        return await new HistoryChainRewriteExecutor(runtime, engine, archive).PrepareAsync(plan, progress, token).ConfigureAwait(false);
    }

    public static async Task<HistoryChainRewriteResult> CommitVersionDeletionAsync(BackupConfig config,
        PreparedHistoryChainRewrite prepared, CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var archive = new SevenZipHistoryArchiveBackend(config);
        return await new HistoryChainRewriteExecutor(runtime, RewriteEngine(archive), archive).CommitAsync(prepared, token).ConfigureAwait(false);
    }

    private static RepresentationRuntime RewriteEngine(SevenZipHistoryArchiveBackend archive)
        => new([new CoreArchiveRepresentationHandler(archive), new SmartDeltaRepresentationHandler(archive)]);
}
