using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal static async Task<MergeSession?> StartMergeAsync(BackupConfig config, BranchId source, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        if (!NativeHistoryRestoreOrchestrator.IsCoordinatorAvailable(config, out var diagnostic))
            throw new InvalidOperationException(diagnostic);
        var restore = CreateRestoreService(config, runtime);
        await restore.RecoverIncompleteAsync(token).ConfigureAwait(false);
        return await new HistoryMergeService(runtime, restore).StartAsync(source, NativeHistoryConfigLease.Signature(config),
            await BindingsAsync(config, config.SourceFolders, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    internal static async Task<MergeSession> RecomputeMergeAsync(BackupConfig config, MergeSession session, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        return await new HistoryMergeService(runtime, CreateRestoreService(config, runtime)).RecomputeAsync(session,
            NativeHistoryConfigLease.Signature(config), await BindingsAsync(config, config.SourceFolders, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    internal static Task<HistoryRestoreResult> ApplyMergeAsync(BackupConfig config, MergeSession session, CancellationToken token)
        => new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, config.SourceFolders.ToArray(), session.Id.ToString(),
            async ct =>
            {
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, ct).ConfigureAwait(false);
                var restore = CreateRestoreService(config, runtime);
                var archives = new SevenZipHistoryArchiveBackend(config);
                var builder = new HistoryMergeCommitBuilder(runtime, restore, archives, archives,
                    (version, staging, cancellation) => CaptureMergeMetadataAsync(config, version, staging, cancellation));
                var result = await new HistoryMergeApplyService(runtime, restore, builder,
                    new SafetySnapshotWorkingStateProtector(config, runtime, SafetySnapshotReason.BeforeMerge),
                    async cancellation => (NativeHistoryConfigLease.Signature(config),
                        await BindingsAsync(config, config.SourceFolders, cancellation).ConfigureAwait(false)))
                    .ApplyAsync(session, ct).ConfigureAwait(false);
                if (result.Succeeded)
                    await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(config, result.AppliedSources, ct).ConfigureAwait(false);
                return result;
            }, token, WorkspaceOperationKind.Merge);

    private static async Task<IReadOnlyList<VersionMetadataSnapshot>> CaptureMergeMetadataAsync(BackupConfig config,
        SourceVersion version, string staging, CancellationToken token)
    {
        try
        {
            var folder = config.SourceFolders.Single(f => Source(f) == version.SourceId);
            await using var session = await PluginV3BackupSession.PrepareAsync(config, folder, token).ConfigureAwait(false);
            var result = await session.CaptureVersionMetadataAsync(token, staging).ConfigureAwait(false);
            foreach (var diagnostic in result.Diagnostics) LogService.LogWarning(diagnostic.Message, "Merge metadata");
            return result.Candidates.Select(c => VersionMetadataSnapshot.Create(version.VersionId, c.ProducerPluginId,
                c.SchemaId, c.SchemaVersion, c.Payload, DateTimeOffset.UtcNow)).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.LogWarning($"Merged state metadata was skipped: {ex.Message}", "Merge metadata");
            return [];
        }
    }
}
