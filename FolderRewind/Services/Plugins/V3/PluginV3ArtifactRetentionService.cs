using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3ArtifactRetentionService
{
    public static async ValueTask<BackupService.DeleteBackupResult> DeleteAsync(
        BackupConfig config,
        ManagedFolder folder,
        HistoryItem history,
        BackupDeleteMode mode)
    {
        if (mode == BackupDeleteMode.LocalArchiveOnly)
        {
            return new BackupService.DeleteBackupResult
            {
                Success = false,
                Message = "A committed Artifact cannot be physically deleted while its History root is retained."
            };
        }

        var store = new FileArtifactLedgerStore(config.DestinationPath);
        await store.RecoverAsync();
        var ledger = await store.LoadAsync();
        var root = ledger.HistoryRoots.SingleOrDefault(value =>
            string.Equals(value.HistoryItemId, history.Id, StringComparison.Ordinal));
        if (root is null || root.RootArtifactId.Value != history.ArtifactRootId)
        {
            return new BackupService.DeleteBackupResult
            {
                Success = false,
                Message = "History and Artifact Ledger roots do not match."
            };
        }

        await store.RemoveHistoryRootAsync(
            history.Id,
            new ArtifactGraphRevision(Guid.NewGuid().ToString("N")));
        HistoryService.RemoveEntry(history);
        var removedArtifacts = mode == BackupDeleteMode.LocalArchiveAndRecord
            ? await store.GarbageCollectUnreachableAsync()
            : Array.Empty<ArtifactId>();
        var archivePath = HistoryService.GetBackupFilePath(config, folder, history);
        return new BackupService.DeleteBackupResult
        {
            Success = true,
            HistoryUpdated = true,
            ArchiveDeleted = mode == BackupDeleteMode.LocalArchiveAndRecord
                && (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath)),
            Message = removedArtifacts.Count == 0 && mode == BackupDeleteMode.LocalArchiveAndRecord
                ? "History root removed; shared Artifact dependencies were retained."
                : string.Empty
        };
    }
}
