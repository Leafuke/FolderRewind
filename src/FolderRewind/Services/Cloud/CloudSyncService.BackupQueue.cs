using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private static CloudCommandContext BuildSampleContext(BackupConfig config)
        {
            var sampleFolder = config.SourceFolders.FirstOrDefault();
            string folderName = !string.IsNullOrWhiteSpace(sampleFolder?.DisplayName) ? sampleFolder.DisplayName : "SampleFolder";
            string sourcePath = !string.IsNullOrWhiteSpace(sampleFolder?.Path) ? sampleFolder.Path : @"C:\Data\SampleFolder";
            string destinationPath = !string.IsNullOrWhiteSpace(config.DestinationPath) ? config.DestinationPath : @"D:\FolderRewind-Backup";
            string format = string.IsNullOrWhiteSpace(config.Archive?.Format) ? "7z" : config.Archive.Format;
            string archiveFileName = FormattableString.Invariant(
                $"[Full][{DateTime.Now:yyyy-MM-dd_HH-mm-ss}]Sample.{format}");
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
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
            };
        }

    }
}
