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

        /// <summary>
        /// 从元数据目录加载状态与（可选）指定归档的变更记录；metaDir 为空时返回空结果。
        /// </summary>
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

        /// <summary>
        /// 把存储层加载结果（状态 + 各归档记录）转换为旧聚合模型 <see cref="BackupMetadata"/>，
        /// 记录按创建时间排序，供尚未迁移到存储层 API 的调用方使用。
        /// </summary>
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

        /// <summary>
        /// 兜底规范化：确保 FileStates 使用忽略大小写的比较器，且记录集合与各字符串/列表字段非空，
        /// 避免旧版本或外部写入的元数据携带 null 字段。
        /// </summary>
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

        /// <summary>
        /// 对比当前与上次的文件状态字典，产出新增/修改/删除三组相对路径
        /// （键比较忽略大小写，三组结果均已排序）。"修改"的判定口径为文件大小或
        /// 最后写入时间（UTC）任一变化——全应用的"文件变更"定义以此为准。
        /// </summary>
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


        /// <summary>
        /// 备份成功后持久化元数据：写入新的状态（Version 3.0）与本次变更记录。
        /// </summary>
        /// <remarks>
        /// FullFileList 仅在备份类型为 Full 时保留完整文件清单，增量/覆写记录只存差量，
        /// 以控制元数据体积；增量记录的 BasedOnFullBackup 保持指向链首的 Full 归档。
        /// metaDir 为空时视为成功（没有可写的元数据）。
        /// </remarks>
        private static async Task<bool> UpdateMetadataAsync(
            string sourceDir,
            string metaDir,
            string currentBackupFile,
            string baseBackupFile,
            string backupType,
            BackupMetadataState? previousState,
            Dictionary<string, FileState>? states = null,
            BackupChangeSet? changeSet = null,
            FilterSettings? filters = null)
        {
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return true;
            }

            states ??= ScanDirectory(sourceDir, filters);
            changeSet ??= CompareFileStates(states, previousState?.FileStates);
            string previousLastBackupFileName = previousState?.LastBackupFileName ?? string.Empty;

            var state = new BackupMetadataState
            {
                Version = "3.0",
                LastBackupTime = DateTime.Now,
                LastBackupFileName = currentBackupFile,
                BasedOnFullBackup = string.IsNullOrWhiteSpace(baseBackupFile) ? currentBackupFile : baseBackupFile,
                FileStates = states
            };

            var record = new BackupChangeRecord
            {
                ArchiveFileName = currentBackupFile,
                BackupType = backupType,
                BasedOnFullBackup = state.BasedOnFullBackup,
                PreviousBackupFileName = previousLastBackupFileName,
                CreatedAtUtc = DateTime.UtcNow,
                AddedFiles = changeSet.AddedFiles.ToList(),
                ModifiedFiles = changeSet.ModifiedFiles.ToList(),
                DeletedFiles = changeSet.DeletedFiles.ToList(),
                FullFileList = string.Equals(backupType, "Full", StringComparison.OrdinalIgnoreCase)
                    ? states.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>()
            };

            bool saved = await BackupMetadataStoreService.SaveAsync(metaDir, state, record).ConfigureAwait(false);
            if (!saved)
            {
                Log(I18n.GetString("BackupMetadataStore_Log_WriteFailedSimple"), LogLevel.Error);
            }

            return saved;
        }

        /// <summary>
        /// 归档被删除或经安全删除改名后，委托存储层同步元数据引用；失败仅记警告，不阻断删除流程。
        /// </summary>
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

