using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
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
        // 删除、裁剪和安全删除逻辑放在一起，避免增量链维护散落到备份主流程里。

        /// <summary>
        /// 删除一个历史条目对应的备份。路由顺序：
        /// 被保留的运行记录引用时拒绝 → 产物图条目（ArtifactRootId）委托产物保留服务 →
        /// RecordOnly 仅删历史记录 → LocalArchiveAndRecord 删除归档文件与记录。
        /// 成功删除或改名后排队云端配置历史同步。
        /// </summary>
        public static async Task<DeleteBackupResult> DeleteBackupAsync(BackupConfig config, ManagedFolder folder, HistoryItem historyItem, BackupDeleteMode deleteMode)
        {
            if (config == null || folder == null || historyItem == null)
            {
                return new DeleteBackupResult
                {
                    Success = false,
                    Message = I18n.GetString("History_Delete_InvalidRequest")
                };
            }

            if (!string.IsNullOrWhiteSpace(historyItem.Id)
                && BackupRunService.IsHistoryItemReferenced(historyItem.Id))
            {
                return new DeleteBackupResult
                {
                    Success = false,
                    Message = "This archive is referenced by a retained configuration backup run."
                };
            }

            if (historyItem.ArtifactRootId.HasValue)
            {
                var artifactDeletion = await PluginV3ArtifactRetentionService.DeleteAsync(
                    config,
                    folder,
                    historyItem,
                    deleteMode);
                if (artifactDeletion.Success)
                {
                    CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(config, "artifact history deletion");
                }
                return artifactDeletion;
            }

            if (deleteMode == BackupDeleteMode.RecordOnly)
            {
                HistoryService.RemoveEntry(historyItem);
                CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(config, "record deletion");
                return new DeleteBackupResult
                {
                    Success = true,
                    ArchiveDeleted = false,
                    HistoryUpdated = true
                };
            }

            var deleteOperationResult = await Task.Run(async () =>
            {
                string? targetFilePath = HistoryService.GetBackupFilePath(config, folder, historyItem);
                if (string.IsNullOrWhiteSpace(targetFilePath))
                {
                    return new DeleteBackupResult
                    {
                        Success = false,
                        Message = "Invalid backup path in history record."
                    };
                }

                var targetFile = new FileInfo(targetFilePath);
                var backupDir = targetFile.Directory;
                if (backupDir == null)
                {
                    return new DeleteBackupResult
                    {
                        Success = false,
                        Message = "Invalid backup directory."
                    };
                }

                string backupFolderName = backupDir.Name;
                string format = targetFile.Extension.TrimStart('.');
                if (string.IsNullOrWhiteSpace(format))
                {
                    format = config.Archive.Format;
                }

                var deleteResult = await DeleteBackupArchiveInternalAsync(
                    targetFile,
                    backupDir,
                    format,
                    config,
                    backupFolderName,
                    config.Archive.SafeDeleteEnabled,
                    deleteMode == BackupDeleteMode.LocalArchiveAndRecord).ConfigureAwait(false);

                return new DeleteBackupResult
                {
                    Success = deleteResult.Success,
                    ArchiveDeleted = deleteResult.ArchiveDeleted,
                    HistoryUpdated = deleteResult.HistoryUpdated,
                    Message = deleteResult.Message
                };
            });

            if (deleteOperationResult.Success && (deleteOperationResult.HistoryUpdated || deleteOperationResult.ArchiveDeleted))
            {
                CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(config, "manual backup deletion");
            }

            return deleteOperationResult;
        }

        /// <summary>
        /// 归档删除执行体：前后各清理一次 7z 残留临时文件；启用安全删除且找到后继增量时
        /// 走 TrySafeDeleteArchive 重建合并，否则直接删除文件（不存在的文件视为成功）。
        /// 删除成功后同步历史（改名引用重写、按需移除条目）并调用元数据同步。
        /// </summary>
        private static async Task<DeleteArchiveExecutionResult> DeleteBackupArchiveInternalAsync(
            FileInfo fileToDelete,
            DirectoryInfo backupDir,
            string format,
            BackupConfig? config = null,
            string? folderName = null,
            bool safeDeleteEnabled = true,
            bool removeHistoryEntry = true)
        {
            var result = new DeleteArchiveExecutionResult
            {
                DeletedFileName = fileToDelete.Name
            };

            string resolvedFolderName = string.IsNullOrWhiteSpace(folderName) ? backupDir.Name : folderName;
            string normalizedFormat = string.IsNullOrWhiteSpace(format)
                ? fileToDelete.Extension.TrimStart('.')
                : format.TrimStart('.');

            try
            {
                if (backupDir.Exists)
                {
                    CleanupArchiveTempArtifacts(backupDir, normalizedFormat);

                    if (fileToDelete.Exists && safeDeleteEnabled && TryGetSafeDeleteSuccessor(fileToDelete, backupDir, normalizedFormat, config, resolvedFolderName, out var nextFile))
                    {
                        result.Success = TrySafeDeleteArchive(fileToDelete, nextFile!, backupDir, normalizedFormat, config, resolvedFolderName, result);
                    }
                    else
                    {
                        if (fileToDelete.Exists)
                        {
                            try
                            {
                                if ((fileToDelete.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                {
                                    fileToDelete.Attributes &= ~FileAttributes.ReadOnly;
                                }
                            }
                            catch
                            {
                            }

                            fileToDelete.Delete();
                            result.ArchiveDeleted = true;
                            Log(I18n.Format("BackupService_Log_PrunedOldBackup", fileToDelete.Name), LogLevel.Info);
                        }
                        else
                        {
                            Log(I18n.Format("BackupService_Log_BackupFileNotFound", fileToDelete.FullName), LogLevel.Warning);
                        }

                        result.Success = true;
                    }

                    CleanupArchiveTempArtifacts(backupDir, normalizedFormat);
                }
                else
                {
                    result.Success = true;
                }

                if (result.Success && config != null)
                {
                    if (!string.IsNullOrWhiteSpace(result.RenamedFromFileName)
                        && !string.IsNullOrWhiteSpace(result.RenamedToFileName))
                    {
                        HistoryService.RenameEntriesForFile(
                            config.Id,
                            resolvedFolderName,
                            result.RenamedFromFileName,
                            result.RenamedToFileName,
                            result.RenamedToBackupType);
                    }

                    int removedCount = 0;
                    if (removeHistoryEntry)
                    {
                        removedCount = HistoryService.RemoveEntriesForFile(config.Id, resolvedFolderName, result.DeletedFileName);
                    }

                    result.HistoryUpdated = removedCount > 0 || !string.IsNullOrWhiteSpace(result.RenamedFromFileName);

                    await SynchronizeMetadataAfterArchiveDeletionAsync(
                        config,
                        resolvedFolderName,
                        result.DeletedFileName,
                        result.RenamedFromFileName,
                        result.RenamedToFileName,
                        result.RenamedToBackupType).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                Log(I18n.Format("BackupService_Log_PruneDeleteFailed", fileToDelete.Name, ex.Message), LogLevel.Warning);
            }

            return result;
        }

        /// <summary>
        /// 为安全删除寻找后继增量：目标必须是链成员（Full 或增量），且按时间排序的
        /// 紧邻下一个文件是增量归档。链尾或后继非增量时返回 false（退化为普通删除）。
        /// </summary>
        private static bool TryGetSafeDeleteSuccessor(
            FileInfo fileToDelete,
            DirectoryInfo backupDir,
            string format,
            BackupConfig? config,
            string? folderName,
            out FileInfo? nextFile)
        {
            nextFile = null;
            if (!fileToDelete.Exists || !backupDir.Exists)
            {
                return false;
            }

            bool currentIsChainArchive = IsFullBackupFile(fileToDelete, config, folderName)
                || IsIncrementalBackupFile(fileToDelete, config, folderName);
            if (!currentIsChainArchive)
            {
                return false;
            }

            var allFiles = backupDir.GetFiles($"*.{format}")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int index = allFiles.FindIndex(f => string.Equals(f.FullName, fileToDelete.FullName, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= allFiles.Count)
            {
                return false;
            }

            var candidate = allFiles[index + 1];
            if (!IsIncrementalBackupFile(candidate, config, folderName))
            {
                return false;
            }

            nextFile = candidate;
            return true;
        }

        /// <summary>
        /// 安全删除备份文件：将当前节点与它的后继 Smart 节点重建成一个新的后继归档，避免直接在原归档旁生成 7z 的 .tmp 临时文件。
        /// </summary>
        /// <remarks>
        /// 流程：把两个归档先后解压到同一合并目录 → 重新压缩为单个归档 → 原后继先挪到暂存名，
        /// 新归档就位后删除原节点（替换失败会把暂存文件移回原位回滚）。
        /// 删除的是 Full 且后继是 Smart 时，新归档文件名中的 [Smart] 改写为 [Full]（晋升为全量），
        /// 并保留后继的原修改时间以维持链序。最终产物在临时目录中构建，finally 尽力清理。
        /// </remarks>
        private static bool TrySafeDeleteArchive(
            FileInfo fileToDelete,
            FileInfo nextFile,
            DirectoryInfo backupDir,
            string format,
            BackupConfig? config,
            string? folderName,
            DeleteArchiveExecutionResult result)
        {
            Log(I18n.Format("BackupService_Log_SafeDeleteStart", fileToDelete.Name), LogLevel.Info);

            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe))
            {
                result.Message = I18n.GetString("BackupService_Log_SafeDeleteNo7z");
                Log(result.Message, LogLevel.Warning);
                return false;
            }

            string tempDir = Path.Combine(backupDir.FullName, "__FolderRewind_SafeDelete_" + Guid.NewGuid().ToString("N"));
            string mergeDir = Path.Combine(tempDir, "merged");
            string stagedNextPath = Path.Combine(tempDir, nextFile.Name + ".original");
            string? safeDeletePassword = config != null ? ResolvePassword(config) : null;
            if (config?.IsEncrypted == true && string.IsNullOrWhiteSpace(safeDeletePassword))
            {
                result.Message = MissingEncryptionPasswordMessage;
                Log(result.Message, LogLevel.Error);
                return false;
            }
            var archiveSettings = CreateArchiveSettingsForSafeDelete(config?.Archive, format);

            try
            {
                Directory.CreateDirectory(mergeDir);

                Log(I18n.Format("BackupService_Log_SafeDeleteStep1"), LogLevel.Info);
                if (!ExtractArchiveToDirectorySync(sevenZipExe, fileToDelete.FullName, mergeDir, safeDeletePassword, archiveSettings.CpuThreads, archiveSettings.RunCompressionAtLowPriority))
                {
                    result.Message = I18n.GetString("BackupService_Log_SafeDeleteExtractFailed");
                    Log(result.Message, LogLevel.Error);
                    return false;
                }

                if (!ExtractArchiveToDirectorySync(sevenZipExe, nextFile.FullName, mergeDir, safeDeletePassword, archiveSettings.CpuThreads, archiveSettings.RunCompressionAtLowPriority))
                {
                    result.Message = I18n.GetString("BackupService_Log_SafeDeleteExtractFailed");
                    Log(result.Message, LogLevel.Error);
                    return false;
                }

                Log(I18n.Format("BackupService_Log_SafeDeleteStep2"), LogLevel.Info);
                string rebuiltArchivePath = Path.Combine(tempDir, "merged." + archiveSettings.Format);
                if (!CreateArchiveFromDirectorySync(sevenZipExe, mergeDir, rebuiltArchivePath, archiveSettings, safeDeletePassword))
                {
                    result.Message = I18n.GetString("BackupService_Log_SafeDeleteMergeFailed");
                    Log(result.Message, LogLevel.Error);
                    return false;
                }

                bool promoteToFull = IsFullBackupFile(fileToDelete, config, folderName)
                    && IsIncrementalBackupFile(nextFile, config, folderName);
                string finalFileName = promoteToFull && nextFile.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase)
                    ? nextFile.Name.Replace("[Smart]", "[Full]", StringComparison.OrdinalIgnoreCase)
                    : nextFile.Name;
                string finalPath = Path.Combine(backupDir.FullName, finalFileName);
                DateTime originalModTime = nextFile.LastWriteTimeUtc;

                File.Move(nextFile.FullName, stagedNextPath);
                try
                {
                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(rebuiltArchivePath, finalPath);
                    File.SetLastWriteTimeUtc(finalPath, originalModTime);

                    try
                    {
                        if ((fileToDelete.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            fileToDelete.Attributes &= ~FileAttributes.ReadOnly;
                        }
                    }
                    catch
                    {
                    }

                    fileToDelete.Delete();
                    result.ArchiveDeleted = true;

                    if (File.Exists(stagedNextPath))
                    {
                        File.Delete(stagedNextPath);
                    }
                }
                catch (Exception replaceEx)
                {
                    try
                    {
                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (File.Exists(stagedNextPath))
                        {
                            File.Move(stagedNextPath, nextFile.FullName);
                        }
                    }
                    catch
                    {
                    }

                    result.Message = replaceEx.Message;
                    Log(I18n.Format("BackupService_Log_SafeDeleteFatalError", replaceEx.Message), LogLevel.Error);
                    return false;
                }

                if (!string.Equals(finalFileName, nextFile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.RenamedFromFileName = nextFile.Name;
                    result.RenamedToFileName = finalFileName;
                    result.RenamedToBackupType = "Full";
                    Log(I18n.Format("BackupService_Log_SafeDeleteRenamed", finalFileName), LogLevel.Info);
                }

                Log(I18n.Format("BackupService_Log_SafeDeleteSuccess", fileToDelete.Name), LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                Log(I18n.Format("BackupService_Log_SafeDeleteFatalError", ex.Message), LogLevel.Error);
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        ClearReadonlyAttributes(tempDir);
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                }
            }
        }
    }
}

