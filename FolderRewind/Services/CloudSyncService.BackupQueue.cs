using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class CloudSyncService
    {
        public static void QueueUploadAfterBackup(BackupConfig? config, ManagedFolder? folder, string? archiveFileName, string? comment)
        {
            if (config?.Cloud?.Enabled != true || folder == null || string.IsNullOrWhiteSpace(archiveFileName))
            {
                return;
            }

            var context = BuildRuntimeContext(config, folder, archiveFileName!, comment);
            var settings = config.Cloud;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteConfiguredUploadAsync(config, folder, settings, context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName, ex.Message), nameof(CloudSyncService));
                }
            });
        }

        private static CloudCommandContext BuildRuntimeContext(BackupConfig config, ManagedFolder folder, string archiveFileName, string? comment)
        {
            string destinationPath = config.DestinationPath ?? string.Empty;
            string backupSubDir = Path.Combine(destinationPath, folder.DisplayName ?? string.Empty);
            string metadataDir = Path.Combine(destinationPath, "_metadata", folder.DisplayName ?? string.Empty);
            if (BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationPath,
                folder.DisplayName ?? string.Empty,
                folder.Path,
                out _,
                out var resolvedBackupSubDir,
                out var resolvedMetadataDir))
            {
                backupSubDir = resolvedBackupSubDir;
                metadataDir = resolvedMetadataDir;
            }

            return new CloudCommandContext
            {
                ConfigName = config.Name ?? string.Empty,
                ConfigId = config.Id ?? string.Empty,
                FolderName = folder.DisplayName ?? string.Empty,
                SourcePath = folder.Path ?? string.Empty,
                DestinationPath = config.DestinationPath ?? string.Empty,
                BackupSubDir = backupSubDir,
                MetadataDir = metadataDir,
                ArchiveFileName = archiveFileName,
                ArchiveFilePath = Path.Combine(backupSubDir, archiveFileName),
                BackupMode = config.Archive?.Mode.ToString() ?? BackupMode.Full.ToString(),
                Comment = comment ?? string.Empty,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            };
        }

        private static CloudCommandContext BuildSampleContext(BackupConfig config)
        {
            var sampleFolder = config.SourceFolders.FirstOrDefault();
            string folderName = !string.IsNullOrWhiteSpace(sampleFolder?.DisplayName) ? sampleFolder.DisplayName : "SampleFolder";
            string sourcePath = !string.IsNullOrWhiteSpace(sampleFolder?.Path) ? sampleFolder.Path : @"C:\Data\SampleFolder";
            string destinationPath = !string.IsNullOrWhiteSpace(config.DestinationPath) ? config.DestinationPath : @"D:\FolderRewind-Backup";
            string format = string.IsNullOrWhiteSpace(config.Archive?.Format) ? "7z" : config.Archive.Format;
            string archiveFileName = $"[Full][{DateTime.Now:yyyy-MM-dd_HH-mm-ss}]Sample.{format}";
            string backupSubDir = Path.Combine(destinationPath, folderName);
            string metadataDir = Path.Combine(destinationPath, "_metadata", folderName);
            if (BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationPath,
                folderName,
                sourcePath,
                out _,
                out var resolvedBackupSubDir,
                out var resolvedMetadataDir))
            {
                backupSubDir = resolvedBackupSubDir;
                metadataDir = resolvedMetadataDir;
            }

            return new CloudCommandContext
            {
                ConfigName = string.IsNullOrWhiteSpace(config.Name) ? "DefaultConfig" : config.Name,
                ConfigId = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString() : config.Id,
                FolderName = folderName,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                BackupSubDir = backupSubDir,
                MetadataDir = metadataDir,
                ArchiveFileName = archiveFileName,
                ArchiveFilePath = Path.Combine(backupSubDir, archiveFileName),
                BackupMode = config.Archive?.Mode.ToString() ?? BackupMode.Full.ToString(),
                Comment = "ManualBackup",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            };
        }

    }
}
