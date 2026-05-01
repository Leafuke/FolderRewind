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
        public static ObservableCollection<BackupTask> ActiveTasks { get; } = new();

        // 还原阶段会用内部标记目录记录“仅删除”动作，完成后必须清理避免污染用户目录。
        private const string InternalRestoreMarkerDirectoryName = "__FolderRewind_Internal";
        private const string InternalRestoreMarkerFileName = "__DeletedOnly.marker";
        private const string MissingEncryptionPasswordMessage = "Encrypted backup password is missing for this configuration.";

        // 变更集是增量备份/删除标记/元数据写入的统一输入。
        private sealed class BackupChangeSet
        {
            public List<string> AddedFiles { get; } = new();
            public List<string> ModifiedFiles { get; } = new();
            public List<string> DeletedFiles { get; } = new();

            public bool HasChanges => AddedFiles.Count > 0
                || ModifiedFiles.Count > 0
                || DeletedFiles.Count > 0;
        }

        private sealed class SmartRestoreArchiveGroup
        {
            public required FileInfo Archive { get; init; }
            public required List<string> Files { get; init; }
        }

        private sealed class SmartRestorePlan
        {
            public required List<FileInfo> Chain { get; init; }
            public required List<SmartRestoreArchiveGroup> ArchiveGroups { get; init; }
        }

        private enum RestoreChainBuildStatus
        {
            Success = 0,
            MissingBaseFull = 1,
            NotFound = 2
        }

        public sealed class DeleteBackupResult
        {
            public bool Success { get; init; }
            public bool ArchiveDeleted { get; init; }
            public bool HistoryUpdated { get; init; }
            public string Message { get; init; } = string.Empty;
        }

        private sealed class DeleteArchiveExecutionResult
        {
            public bool Success { get; set; }
            public bool ArchiveDeleted { get; set; }
            public bool HistoryUpdated { get; set; }
            public string DeletedFileName { get; set; } = string.Empty;
            public string? RenamedFromFileName { get; set; }
            public string? RenamedToFileName { get; set; }
            public string? RenamedToBackupType { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        private sealed class PruneArchivesResult
        {
            public bool Success { get; set; } = true;
            public int DeletedCount { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        private static readonly object SevenZipResolutionLock = new();
        private static string? _cachedSevenZipExecutable;
        private static string? _cachedSevenZipConfigPath;
        private static string? _cachedSevenZipPathEnvironment;


        // 主备份编排保留在入口文件中；压缩、过滤、元数据和还原细节拆到同名 partial 文件。



        /// <summary>
        /// 备份配置下的所有文件夹
        /// </summary>
        /// <returns>true 表示至少有一个文件夹产生了新的备份文件；false 表示所有文件夹均未检测到变更。</returns>
        public static async Task<bool> BackupConfigAsync(BackupConfig config)
        {
            if (config == null) return false;
            Log(I18n.Format("BackupService_Log_ConfigTaskBegin", config.Name), LogLevel.Info);

            bool anyChanges = false;
            foreach (var folder in config.SourceFolders)
            {
                var hadChanges = await BackupFolderAsync(config, folder);
                if (hadChanges) anyChanges = true;
            }

            Log(I18n.Format("BackupService_Log_TaskEnd"), LogLevel.Info);
            return anyChanges;
        }

        /// <summary>
        /// 备份单个文件夹
        /// </summary>
        /// <returns>true 表示产生了新的备份文件；false 表示未检测到变更或备份失败。</returns>
        public static async Task<bool> BackupFolderAsync(BackupConfig config, ManagedFolder folder, string? comment = "", bool forceFullBackup = false)
        {
            if (config == null || folder == null) return false;
            comment ??= string.Empty;

            int configIndex = GetConfigIndex(config);

            // 1. 创建任务对象并确保在 UI 线程添加到集合
            var task = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = I18n.Format("BackupService_Task_Preparing"),
                Progress = 0
            };

            await RunOnUIAsync(() => ActiveTasks.Insert(0, task));

            // 检查是否有插件希望完全接管备份流程
            var (shouldHandle, handlerPlugin) = Services.Plugins.PluginService.CheckPluginWantsToHandleBackup(config);
            if (shouldHandle && handlerPlugin != null)
            {
                return await HandlePluginBackupAsync(config, folder, task, handlerPlugin, comment);
            }

            // 允许插件在备份前创建快照并替换源路径（例如 Minecraft 热备份：先复制到 snapshot 再备份）。
            string sourcePath = folder.Path;
            try
            {
                var pluginOverride = Services.Plugins.PluginService.InvokeBeforeBackupFolder(config, folder);
                if (!string.IsNullOrWhiteSpace(pluginOverride))
                {
                    sourcePath = pluginOverride;
                }
            }
            catch
            {
                // 插件异常不会影响核心备份流程（具体异常会在 PluginService 内记录）
            }
            if (!Directory.Exists(sourcePath))
            {
                Log(I18n.Format("BackupService_Log_SourceFolderMissing", sourcePath), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_SourceNotFound");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = I18n.Format("BackupService_Folder_SourceNotFound");
                });

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=command_failed");
                }
                catch
                {
                }
                return false;
            }

            if (string.IsNullOrEmpty(config.DestinationPath))
            {
                Log(I18n.Format("BackupService_Log_DestinationNotSet"), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_TargetNotSet");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = I18n.Format("BackupService_Folder_TargetNotSet");
                });

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=command_failed");
                }
                catch
                {
                }
                return false;
            }

            if (!TryResolveBackupStoragePaths(
                config.DestinationPath,
                folder.DisplayName,
                folder.Path,
                out var storageFolderName,
                out var backupSubDir,
                out var metadataDir))
            {
                string invalidFolderNameMessage = I18n.GetString("BackupService_Log_InvalidStorageFolderName");
                Log(invalidFolderNameMessage, LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Task_Failed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = invalidFolderNameMessage;
                });

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=invalid_folder_name");
                }
                catch
                {
                }
                return false;
            }

            // 创建必要的目录
            if (!Directory.Exists(backupSubDir)) Directory.CreateDirectory(backupSubDir);
            if (!Directory.Exists(metadataDir)) Directory.CreateDirectory(metadataDir);

            Log(I18n.Format("BackupService_Log_ProcessingFolder", folder.DisplayName), LogLevel.Info);
            await RunOnUIAsync(() => folder.StatusText = I18n.Format("BackupService_Folder_BackupInProgress"));

            // 与 MineBackup 保持一致：备份开始事件
            try
            {
                KnotLinkService.BroadcastEvent($"event=backup_started;config={configIndex};world={folder.DisplayName}");
            }
            catch
            {
            }

            bool success = false;
            string? generatedFileName = null;
            try
            {

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Processing");
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupRunning");
                });

                // 调用核心逻辑，传入 task 以便更新进度

                // FORCE_FULL 指令可绕过当前配置模式，直接执行一次全量备份。
                if (forceFullBackup)
                {
                    var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                    success = res.Success;
                    generatedFileName = res.FileName;
                }
                else
                {
                    // 根据模式分发逻辑
                    switch (config.Archive.Mode)
                    {
                        case BackupMode.Incremental:
                            {
                                var res = await DoSmartBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                                success = res.Success;
                                generatedFileName = res.FileName;
                                break;
                            }
                        case BackupMode.Overwrite:
                            {
                                var res = await DoOverwriteBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                                success = res.Success;
                                generatedFileName = res.FileName;
                                break;
                            }
                        case BackupMode.Full:
                        default:
                            {
                                var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                                success = res.Success;
                                generatedFileName = res.FileName;
                                break;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_Exception", ex.Message), LogLevel.Error);
                success = false;
                await RunOnUIAsync(() => { if (string.IsNullOrEmpty(task.ErrorMessage)) task.ErrorMessage = ex.Message; });
            }

            if (success)
            {
                var completedFileName = string.IsNullOrWhiteSpace(generatedFileName) ? null : generatedFileName;
                bool hasNewFile = completedFileName != null;

                if (completedFileName != null)
                {
                    ConfigService.Save();

                    // 增量模式下，根据实际生成的文件名区分 Full 和 Smart
                    string typeStr;
                    if (forceFullBackup)
                    {
                        typeStr = "Full";
                    }
                    else if (config.Archive.Mode == BackupMode.Incremental)
                    {
                        typeStr = completedFileName.StartsWith("[Full]", StringComparison.OrdinalIgnoreCase) ? "Full" : "Smart";
                    }
                    else
                    {
                        typeStr = config.Archive.Mode.ToString();
                    }
                    HistoryService.AddEntry(config, folder, completedFileName, typeStr, comment, storageFolderName);

                    var pruneResult = await Task.Run(() => PruneOldArchives(
                        backupSubDir,
                        config.Archive.Format,
                        config.Archive.KeepCount,
                        config.Archive.Mode,
                        config.Archive.SafeDeleteEnabled,
                        config,
                        folder.DisplayName)).ConfigureAwait(false);

                    if (!pruneResult.Success)
                    {
                        string pruneWarning = I18n.Format("BackupService_Warning_PostBackupCleanupFailed", folder.DisplayName, pruneResult.Message);
                        Log(I18n.Format("BackupService_Log_PostBackupCleanupFailed", folder.DisplayName, pruneResult.Message), LogLevel.Warning);
                        NotificationService.ShowWarning(pruneWarning);
                    }
                    else if (pruneResult.DeletedCount > 0)
                    {
                        CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(config, "automatic archive pruning");
                    }

                    // 备份完成后检查文件大小，过小时发出警告
                    try
                    {
                        var archiveFile = Path.Combine(backupSubDir, completedFileName);
                        if (File.Exists(archiveFile))
                        {
                            var fileSizeKB = new FileInfo(archiveFile).Length / 1024.0;
                            var thresholdKB = ConfigService.CurrentConfig?.GlobalSettings?.FileSizeWarningThresholdKB ?? 5;
                            if (thresholdKB > 0 && fileSizeKB < thresholdKB)
                            {
                                Log(I18n.Format("BackupService_Log_FileSizeTooSmall", folder.DisplayName, fileSizeKB.ToString("F1"), thresholdKB.ToString()), LogLevel.Warning);
                                NotificationService.ShowWarning(
                                    I18n.Format("BackupService_Warning_FileSizeTooSmall", folder.DisplayName, fileSizeKB.ToString("F1"), thresholdKB.ToString()));
                                KnotLinkService.BroadcastEvent($"event=backup_warning;type=file_too_small;config={configIndex};world={folder.DisplayName};size_kb={fileSizeKB:F1}");
                            }
                        }
                    }
                    catch
                    {
                    }

                    // 与 MineBackup 保持一致：备份成功事件
                    try
                    {
                        KnotLinkService.BroadcastEvent($"event=backup_success;config={configIndex};world={folder.DisplayName};file={completedFileName}");
                    }
                    catch
                    {
                    }

                    CloudSyncService.QueueUploadAfterBackup(config, folder, completedFileName, comment);
                }

                await RunOnUIAsync(() =>
                {
                    task.Status = hasNewFile
                        ? I18n.Format("BackupService_Task_Completed")
                        : I18n.Format("BackupService_Task_NoChanges");
                    task.Progress = 100;
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = true;

                    folder.StatusText = hasNewFile
                        ? I18n.Format("BackupService_Folder_BackupCompleted")
                        : I18n.Format("BackupService_Task_NoChanges");
                    if (hasNewFile)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });

                Log(
                    hasNewFile
                        ? I18n.Format("BackupService_Log_BackupSucceeded", folder.DisplayName)
                        : I18n.Format("BackupService_Log_BackupSkippedNoChanges", folder.DisplayName),
                    LogLevel.Info);
            }
            else
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });
                Log(I18n.Format("BackupService_Log_BackupFailed", folder.DisplayName), LogLevel.Error);

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=command_failed");
                }
                catch
                {
                }

                // 发送失败通知
                NotificationService.NotifyBackupCompleted(folder.DisplayName, false, I18n.GetString("BackupService_Task_Failed"));
            }

            // 备份后回调（用于清理快照等）
            try
            {
                Services.Plugins.PluginService.InvokeAfterBackupFolder(config, folder, success, generatedFileName);
            }
            catch
            {
            }

            // 注意：这里返回“是否真的产出新归档”，会影响自动化里的无变更计数策略。
            return success && !string.IsNullOrWhiteSpace(generatedFileName);
        }

        /// <summary>
        /// 由插件完全接管的备份流程
        /// </summary>
        private static async Task<bool> HandlePluginBackupAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupTask task,
            Services.Plugins.IFolderRewindPlugin plugin,
            string comment)
        {
            Log(I18n.Format("BackupService_Log_PluginTakeover", plugin.Manifest.Name, folder.DisplayName), LogLevel.Info);

            await RunOnUIAsync(() =>
            {
                task.Status = I18n.Format("BackupService_Task_PluginProcessing");
                folder.StatusText = I18n.Format("BackupService_Folder_PluginBackingUp");
            });

            try
            {
                var result = await Services.Plugins.PluginService.InvokePluginBackupAsync(
                    plugin, config, folder, comment,
                    async (progress, status) =>
                    {
                        await RunOnUIAsync(() =>
                        {
                            task.Progress = progress;
                            task.Status = status;
                        });
                    });

                if (result.Success)
                {
                    bool hasNewFile = !string.IsNullOrWhiteSpace(result.GeneratedFileName);

                    await RunOnUIAsync(() =>
                    {
                        task.Status = hasNewFile
                            ? I18n.Format("BackupService_Task_Completed")
                            : I18n.Format("BackupService_Task_NoChanges");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;

                        folder.StatusText = hasNewFile
                            ? I18n.Format("BackupService_Folder_BackupCompleted")
                            : I18n.Format("BackupService_Task_NoChanges");
                        if (hasNewFile)
                        {
                            folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                        }
                    });

                    if (hasNewFile)
                    {
                        ConfigService.Save();
                        if (!TryResolveStorageFolderName(folder.DisplayName, folder.Path, out var storageFolderName))
                        {
                            throw new InvalidOperationException(I18n.GetString("BackupService_Log_InvalidStorageFolderName"));
                        }

                        HistoryService.AddEntry(config, folder, result.GeneratedFileName!, "Plugin", comment, storageFolderName);
                        CloudSyncService.QueueUploadAfterBackup(config, folder, result.GeneratedFileName, comment);
                    }

                    Log(
                        hasNewFile
                            ? I18n.Format("BackupService_Log_PluginBackupSucceeded", folder.DisplayName)
                            : I18n.Format("BackupService_Log_PluginBackupSkippedNoChanges", folder.DisplayName),
                        LogLevel.Info);

                    return hasNewFile;
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_Failed");
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = false;
                        task.ErrorMessage = result.Message ?? string.Empty;
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    });

                    Log(I18n.Format("BackupService_Log_PluginBackupFailed", folder.DisplayName, result.Message ?? string.Empty), LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Exception");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                    folder.StatusText = I18n.Format("BackupService_Folder_PluginException");
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// 由插件完全接管的还原流程
        /// </summary>
        private static async Task HandlePluginRestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem historyItem,
            BackupTask task,
            Services.Plugins.IFolderRewindPlugin plugin,
            int configIndex)
        {
            Log(I18n.Format("BackupService_Log_PluginRestoreTakeover", plugin.Manifest.Name, folder.DisplayName), LogLevel.Info);

            await RunOnUIAsync(() =>
            {
                task.Status = I18n.Format("BackupService_Task_PluginProcessing");
                task.Progress = 0;
                task.IsIndeterminate = true;
            });

            bool restoreStarted = false;

            try
            {
                try
                {
                    KnotLinkService.BroadcastEvent($"event=restore_started;config={configIndex};world={folder.DisplayName}");
                    restoreStarted = true;
                }
                catch
                {
                }

                var result = await Services.Plugins.PluginService.InvokePluginRestoreAsync(
                    plugin,
                    config,
                    folder,
                    historyItem.FileName,
                    async (progress, status) =>
                    {
                        await RunOnUIAsync(() =>
                        {
                            task.Progress = progress;
                            task.Status = string.IsNullOrWhiteSpace(status)
                                ? I18n.Format("BackupService_Task_PluginProcessing")
                                : status;
                            task.IsIndeterminate = false;
                        });
                    });

                if (result.Success)
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.GetString("BackupService_Task_RestoreCompleted");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;
                        task.ErrorMessage = string.Empty;
                    });

                    Log(I18n.Format("BackupService_Log_PluginRestoreSucceeded", folder.DisplayName), LogLevel.Info);
                    NotificationService.NotifyRestoreCompleted(folder.DisplayName, true, I18n.GetString("BackupService_Task_RestoreCompleted"));

                    try
                    {
                        KnotLinkService.BroadcastEvent($"event=restore_success;config={configIndex};world={folder.DisplayName};backup={historyItem.FileName}");
                    }
                    catch
                    {
                    }

                    return;
                }

                string failureMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? I18n.GetString("BackupService_Task_RestoreFailed")
                    : result.Message!;

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("BackupService_Task_RestoreFailed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = failureMessage;
                });

                Log(I18n.Format("BackupService_Log_PluginRestoreFailed", folder.DisplayName, failureMessage), LogLevel.Error);

                if (restoreStarted)
                {
                    try
                    {
                        KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=plugin_restore_failed");
                    }
                    catch
                    {
                    }
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, failureMessage);
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("BackupService_Task_RestoreFailed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);

                if (restoreStarted)
                {
                    try
                    {
                        KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=plugin_restore_exception");
                    }
                    catch
                    {
                    }
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, ex.Message);
            }
        }

    }
}
