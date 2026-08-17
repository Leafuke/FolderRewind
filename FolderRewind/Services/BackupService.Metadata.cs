using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        // 元数据兼容层集中维护，继续复用 BackupMetadataStoreService 处理旧格式迁移。

        private static async Task<BackupMetadataStoreService.BackupMetadataLoadResult> LoadBackupMetadataAsync(
            string metaDir,
            IEnumerable<string>? archiveFileNames = null)
        {
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return new BackupMetadataStoreService.BackupMetadataLoadResult();
            }

            return await BackupMetadataStoreService.LoadAsync(metaDir, archiveFileNames).ConfigureAwait(false);
        }

        private static BackupMetadata? LoadBackupMetadata(string metadataPath)
        {
            if (string.IsNullOrWhiteSpace(metadataPath))
            {
                return null;
            }

            string? metaDir = Path.GetDirectoryName(metadataPath);
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return null;
            }

            var loadResult = BackupMetadataStoreService.LoadAsync(metaDir).GetAwaiter().GetResult();
            return ConvertToAggregateMetadata(loadResult);
        }

        private static BackupMetadata? ConvertToAggregateMetadata(BackupMetadataStoreService.BackupMetadataLoadResult loadResult)
        {
            if (loadResult.State == null)
            {
                return null;
            }

            var metadata = new BackupMetadata
            {
                Version = loadResult.State.Version,
                LastBackupTime = loadResult.State.LastBackupTime,
                LastBackupFileName = loadResult.State.LastBackupFileName,
                BasedOnFullBackup = loadResult.State.BasedOnFullBackup,
                FileStates = new Dictionary<string, FileState>(loadResult.State.FileStates, StringComparer.OrdinalIgnoreCase),
                BackupRecords = loadResult.Records.Values
                    .Select(record => new BackupChangeRecord
                    {
                        ArchiveFileName = record.ArchiveFileName,
                        BackupType = record.BackupType,
                        BasedOnFullBackup = record.BasedOnFullBackup,
                        PreviousBackupFileName = record.PreviousBackupFileName,
                        CreatedAtUtc = record.CreatedAtUtc,
                        AddedFiles = record.AddedFiles.ToList(),
                        ModifiedFiles = record.ModifiedFiles.ToList(),
                        DeletedFiles = record.DeletedFiles.ToList(),
                        FullFileList = record.FullFileList.ToList()
                    })
                    .OrderBy(r => r.CreatedAtUtc)
                    .ThenBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            return NormalizeBackupMetadata(metadata);
        }

        private static BackupMetadata NormalizeBackupMetadata(BackupMetadata? meta)
        {
            meta ??= new BackupMetadata();
            meta.FileStates = meta.FileStates != null
                ? new Dictionary<string, FileState>(meta.FileStates, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            meta.BackupRecords ??= new List<BackupChangeRecord>();

            foreach (var record in meta.BackupRecords)
            {
                record.ArchiveFileName ??= string.Empty;
                record.BackupType ??= string.Empty;
                record.BasedOnFullBackup ??= string.Empty;
                record.PreviousBackupFileName ??= string.Empty;
                record.AddedFiles ??= new List<string>();
                record.ModifiedFiles ??= new List<string>();
                record.DeletedFiles ??= new List<string>();
                record.FullFileList ??= new List<string>();
            }

            return meta;
        }

        private static BackupChangeSet CompareFileStates(
            IReadOnlyDictionary<string, FileState> currentStates,
            IReadOnlyDictionary<string, FileState>? previousStates)
        {
            var result = new BackupChangeSet();
            previousStates ??= new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in currentStates)
            {
                if (!previousStates.TryGetValue(kvp.Key, out var oldState))
                {
                    result.AddedFiles.Add(kvp.Key);
                    continue;
                }

                if (kvp.Value.Size != oldState.Size
                    || kvp.Value.LastWriteTimeUtc != oldState.LastWriteTimeUtc)
                {
                    result.ModifiedFiles.Add(kvp.Key);
                }
            }

            foreach (var kvp in previousStates)
            {
                if (!currentStates.ContainsKey(kvp.Key))
                {
                    result.DeletedFiles.Add(kvp.Key);
                }
            }

            result.AddedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            result.ModifiedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            result.DeletedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }


        private static async Task<bool> UpdateMetadataAsync(
            string sourceDir,
            string metaDir,
            string currentBackupFile,
            string baseBackupFile,
            string backupType,
            BackupMetadata? previousMetadata,
            Dictionary<string, FileState>? states = null,
            BackupChangeSet? changeSet = null,
            FilterSettings? filters = null)
        {
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return true;
            }

            states ??= ScanDirectory(sourceDir, filters);
            previousMetadata = NormalizeBackupMetadata(previousMetadata);
            changeSet ??= CompareFileStates(states, previousMetadata.FileStates);
            string previousLastBackupFileName = previousMetadata.LastBackupFileName ?? string.Empty;

            var state = new BackupMetadataState
            {
                Version = "3.0",
                LastBackupTime = DateTime.Now,
                LastBackupFileName = currentBackupFile,
                BasedOnFullBackup = string.IsNullOrWhiteSpace(baseBackupFile) ? currentBackupFile : baseBackupFile,
                FileStates = new Dictionary<string, FileState>(states, StringComparer.OrdinalIgnoreCase)
            };

            var record = new BackupChangeRecord
            {
                ArchiveFileName = currentBackupFile,
                BackupType = backupType,
                BasedOnFullBackup = state.BasedOnFullBackup,
                PreviousBackupFileName = previousLastBackupFileName,
                CreatedAtUtc = DateTime.UtcNow,
                AddedFiles = changeSet.AddedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                ModifiedFiles = changeSet.ModifiedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                DeletedFiles = changeSet.DeletedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                FullFileList = states.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            };

            bool saved = await BackupMetadataStoreService.SaveAsync(metaDir, state, record).ConfigureAwait(false);
            if (!saved)
            {
                Log(I18n.GetString("BackupMetadataStore_Log_WriteFailedSimple"), LogLevel.Error);
            }

            return saved;
        }

        private static async Task SynchronizeMetadataAfterArchiveDeletionAsync(
            BackupConfig config,
            string folderName,
            string deletedFileName,
            string? renamedOldFileName,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            if (config == null
                || string.IsNullOrWhiteSpace(config.DestinationPath)
                || string.IsNullOrWhiteSpace(folderName)
                || string.IsNullOrWhiteSpace(deletedFileName))
            {
                return;
            }

            if (!TryResolveBackupStoragePaths(
                config.DestinationPath,
                folderName,
                fallbackPath: null,
                out _,
                out _,
                out var metadataDir))
            {
                return;
            }

            if (!await BackupMetadataStoreService.SynchronizeAfterArchiveDeletionAsync(
                metadataDir,
                deletedFileName,
                renamedOldFileName,
                renamedNewFileName,
                renamedBackupType).ConfigureAwait(false))
            {
                Log(I18n.GetString("BackupMetadataStore_Log_WriteFailedSimple"), LogLevel.Warning);
            }
        }

    }
}

