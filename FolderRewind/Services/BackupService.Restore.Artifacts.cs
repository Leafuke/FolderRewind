using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services;

public static partial class BackupService
{
    private static async Task<bool?> TryRestoreSemanticArtifactAsync(
        BackupConfig config,
        ManagedFolder folder,
        HistoryItem historyItem,
        RestoreMode requestedMode,
        CancellationToken cancellationToken)
    {
        if (!historyItem.ArtifactRootId.HasValue || string.IsNullOrWhiteSpace(historyItem.Id))
            return null;

        var store = new FileArtifactLedgerStore(config.DestinationPath);
        await store.RecoverAsync(cancellationToken);
        var ledger = await store.LoadAsync(cancellationToken);
        var historyRoot = ledger.HistoryRoots.SingleOrDefault(value =>
            string.Equals(value.HistoryItemId, historyItem.Id, StringComparison.Ordinal));
        if (historyRoot is null || historyRoot.RootArtifactId.Value != historyItem.ArtifactRootId.Value)
            throw new InvalidDataException("History and Artifact Ledger roots do not match.");
        var root = ledger.Artifacts.Single(value => value.ArtifactId == historyRoot.RootArtifactId);
        if (string.Equals(root.RestoreStrategyId.PluginId.Value, "folderrewind.core", StringComparison.Ordinal))
            return null;

        if (config.Archive?.BackupBeforeRestore == true)
        {
            var beforeRestore = await BackupFolderCoreAsync(
                config,
                folder,
                "BeforeRestore",
                BackupInvocationOptions.ForInternal().WithComment("BeforeRestore"),
                createdByRunId: null);
            if (beforeRestore.Status is BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable)
                return false;
            if (beforeRestore.CreatedNewArchive)
                await PruneRetainedSourceArchivesAsync(config);
        }

        var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
        var folderId = Guid.Parse(folder.Id);
        var folderSnapshot = configSnapshot.Folders.Single(value => value.FolderId == folderId);
        var effectiveMode = ResolveEffectiveRestoreMode(historyItem, requestedMode);
        var workspace = Path.Combine(
            config.DestinationPath,
            ".folderrewind",
            "restore-workspaces",
            "restore-" + Guid.NewGuid().ToString("N"));
        var coordinator = new RestoreArtifactMutationCoordinator(
            new RestoreMaterializationCoordinator(PluginV3RuntimeService.Runtime, store));
        var result = await coordinator.ExecuteAsync(
            configSnapshot,
            folderSnapshot,
            historyItem.Id,
            requestedMode == RestoreMode.Clean
                ? FolderRewind.Plugin.Abstractions.RestoreMode.Clean
                : FolderRewind.Plugin.Abstractions.RestoreMode.Overwrite,
            effectiveMode == RestoreMode.Clean
                ? FolderRewind.Plugin.Abstractions.RestoreMode.Clean
                : FolderRewind.Plugin.Abstractions.RestoreMode.Overwrite,
            workspace,
            (materialized, token) => ApplyMaterializedWorkspaceAsync(
                materialized,
                folder.Path,
                config,
                effectiveMode,
                token),
            cancellationToken: cancellationToken);
        return result.TargetMutationStarted
            && result.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings;
    }

    private static async ValueTask<OperationOutcome> ApplyMaterializedWorkspaceAsync(
        string source,
        string target,
        BackupConfig config,
        RestoreMode effectiveMode,
        CancellationToken cancellationToken)
    {
        string? rollbackDirectory = null;
        PathRuleMatcher? whitelist = null;
        try
        {
            if (effectiveMode == RestoreMode.Clean)
            {
                if (config.Filters?.RestoreWhitelist?.Count > 0)
                    whitelist = PathRuleMatcher.CreateForRestore(config.Filters.RestoreWhitelist, target);
                if (!TryPrepareSafeRestoreWorkspace(target, out rollbackDirectory, out var prepareError))
                    throw new IOException(prepareError ?? "Safe Restore workspace preparation failed.");
            }
            else
            {
                Directory.CreateDirectory(target);
            }

            await CopyMaterializedTreeAsync(source, target, cancellationToken);
            CleanupInternalRestoreMarkers(target);
            if (rollbackDirectory is not null
                && !TryCommitSafeRestoreWorkspace(target, rollbackDirectory, whitelist, out var commitError))
            {
                throw new IOException(commitError ?? "Safe Restore commit failed.");
            }
            return OperationOutcome.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (rollbackDirectory is not null)
                TryRollbackSafeRestoreWorkspace(target, rollbackDirectory, out _);
            return OperationOutcome.Canceled;
        }
        catch (Exception ex)
        {
            if (rollbackDirectory is not null)
                TryRollbackSafeRestoreWorkspace(target, rollbackDirectory, out _);
            Log($"[PluginV3] Materialized restore mutation failed: {ex.Message}", LogLevel.Error);
            return OperationOutcome.Failed;
        }
    }

    private static async Task CopyMaterializedTreeAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(source);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }
}
