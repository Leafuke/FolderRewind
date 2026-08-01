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
        public static async Task<(bool Success, int RecoveredCount, string Message)> DownloadConfigurationHistoryAsync(BackupConfig? config)
        {
            if (config == null)
            {
                return (false, 0, I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed"));
            }

            if (!CanUseManualCloudActions(config))
            {
                string message = I18n.GetString("CloudSync_Notification_RcloneOnly");
                NotificationService.ShowWarning(message, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, message);
            }

            if (config.SourceFolders == null || config.SourceFolders.Count == 0)
            {
                string message = I18n.GetString("CloudSync_Notification_ConfigurationNoFolders");
                NotificationService.ShowWarning(message, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, message);
            }

            var analysis = await AnalyzeConfigurationHistoryCoreAsync(config).ConfigureAwait(false);
            if (!analysis.Success)
            {
                NotificationService.ShowError(analysis.Message, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, analysis.Message);
            }

            var importResult = HistoryService.ImportHistoryItems(analysis.MappedItems, merge: true);
            if (!importResult.Success)
            {
                string message = I18n.GetString("CloudSync_ConfigSync_HistoryImportFailed");
                NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, message);
            }

            return await DownloadHistoryItemsAsync(
                config,
                analysis.MappedItems,
                I18n.Format("CloudSync_Task_ConfigurationDownloadName", config.Name ?? string.Empty)).ConfigureAwait(false);
        }

        public static async Task<ConfigCloudHistoryAnalysisResult> AnalyzeConfigurationHistoryAsync(BackupConfig? config)
        {
            return await AnalyzeConfigurationHistoryCoreAsync(config).ConfigureAwait(false);
        }

        public static async Task<ConfigCloudSyncResult> SyncConfigurationFromCloudAsync(BackupConfig? config, ConfigCloudSyncMode mode)
        {
            if (config == null)
            {
                string failureMessage = I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed");
                NotificationService.ShowError(failureMessage, I18n.GetString("CloudSync_Notification_Title"));
                return new ConfigCloudSyncResult
                {
                    Success = false,
                    Message = failureMessage
                };
            }

            var analysis = await AnalyzeConfigurationHistoryCoreAsync(config).ConfigureAwait(false);
            if (!analysis.Success)
            {
                NotificationService.ShowError(analysis.Message, I18n.GetString("CloudSync_Notification_Title"));
                return new ConfigCloudSyncResult
                {
                    Success = false,
                    Message = analysis.Message,
                    Analysis = analysis
                };
            }

            var importResult = HistoryService.ImportHistoryItems(analysis.MappedItems, merge: true);
            if (!importResult.Success)
            {
                string importFailedMessage = I18n.GetString("CloudSync_ConfigSync_HistoryImportFailed");
                NotificationService.ShowError(importFailedMessage, I18n.GetString("CloudSync_Notification_Title"));
                return new ConfigCloudSyncResult
                {
                    Success = false,
                    Message = importFailedMessage,
                    Analysis = analysis
                };
            }

            int recoveredBackupCount = 0;
            bool success = true;
            string message;

            if (mode == ConfigCloudSyncMode.HistoryAndBackups)
            {
                var downloadResult = await DownloadHistoryItemsAsync(
                    config,
                    analysis.MappedItems,
                    I18n.Format("CloudSync_Task_ConfigurationDownloadName", config.Name ?? string.Empty)).ConfigureAwait(false);
                recoveredBackupCount = downloadResult.DownloadedCount;
                success = downloadResult.Success;
                message = success
                    ? I18n.Format("CloudSync_ConfigSync_HistoryAndBackupsSucceeded", importResult.ImportedCount, importResult.DuplicateCount, recoveredBackupCount)
                    : I18n.Format("CloudSync_ConfigSync_HistoryImportedBackupsFailed", importResult.ImportedCount, downloadResult.Message);
            }
            else
            {
                success = true;
                message = I18n.Format("CloudSync_ConfigSync_HistoryOnlySucceeded", importResult.ImportedCount, importResult.DuplicateCount);
            }

            if (success)
            {
                NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
            }
            else
            {
                NotificationService.ShowWarning(message, I18n.GetString("CloudSync_Notification_Title"));
            }

            return new ConfigCloudSyncResult
            {
                Success = success,
                Message = message,
                ImportedHistoryCount = importResult.ImportedCount,
                DuplicateHistoryCount = importResult.DuplicateCount,
                RecoveredBackupCount = recoveredBackupCount,
                Analysis = analysis
            };
        }

        public static async Task<(bool Success, string Message)> ImportConfigFromCloudAsync(string remoteBasePath)
        {
            string remoteConfigPath = AppendRemotePath(remoteBasePath, "config.json");
            return await ImportJsonFromCloudAsync(
                remoteConfigPath,
                I18n.GetString("CloudSync_Task_ConfigImportName"),
                I18n.GetString("CloudSync_Notification_ConfigImportSucceeded"),
                I18n.GetString("CloudSync_Notification_ConfigImportFailed"),
                localPath => ConfigService.ImportConfig(localPath)).ConfigureAwait(false);
        }

        public static async Task<(bool Success, string Message)> ExportConfigToCloudAsync(string remoteBasePath)
        {
            string remoteConfigPath = AppendRemotePath(remoteBasePath, "config.json");
            return await ExportJsonToCloudAsync(
                remoteConfigPath,
                I18n.GetString("CloudSync_Task_ConfigExportName"),
                I18n.GetString("CloudSync_Notification_ConfigExportSucceeded"),
                I18n.GetString("CloudSync_Notification_ConfigExportFailed"),
                localPath => ConfigService.ExportConfig(localPath)).ConfigureAwait(false);
        }

        public static async Task<(bool Success, int Count, string Message)> ImportHistoryFromCloudAsync(string remoteBasePath, bool merge)
        {
            return await ImportHistoryFromCloudCoreAsync(
                AppendRemotePath(remoteBasePath, "history.json"),
                merge,
                I18n.GetString("CloudSync_Task_HistoryImportName"),
                I18n.GetString("CloudSync_Notification_HistoryImportFailed")).ConfigureAwait(false);
        }

        public static async Task<(bool Success, string Message)> ExportHistoryToCloudAsync(string remoteBasePath)
        {
            string remoteHistoryPath = AppendRemotePath(remoteBasePath, "history.json");
            return await ExportJsonToCloudAsync(
                remoteHistoryPath,
                I18n.GetString("CloudSync_Task_HistoryExportName"),
                I18n.GetString("CloudSync_Notification_HistoryExportSucceeded"),
                I18n.GetString("CloudSync_Notification_HistoryExportFailed"),
                localPath => HistoryService.ExportHistory(localPath)).ConfigureAwait(false);
        }

        public static void QueueConfigurationHistorySyncAfterLocalChange(BackupConfig? config, string? reason = null)
        {
            if (config?.Cloud?.Enabled != true || !CanUseManualCloudActions(config))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await UploadConfigurationHistoryAsync(config, showNotifications: false).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        LogService.LogWarning(
                            $"[CloudSyncService] Background config history sync failed for '{config.Name}': {result.Message}",
                            nameof(CloudSyncService));
                    }
                    else if (!string.IsNullOrWhiteSpace(reason))
                    {
                        LogService.LogInfo(
                            $"[CloudSyncService] Background config history sync completed for '{config.Name}' after {reason}.",
                            nameof(CloudSyncService));
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(
                        $"[CloudSyncService] Background config history sync failed: {ex.Message}",
                        nameof(CloudSyncService),
                        ex);
                }
            });
        }

        public static async Task<ConfigCloudHistoryUploadResult> UploadConfigurationHistoryAsync(BackupConfig? config, bool showNotifications = true)

        {
            if (config == null)
            {
                return new ConfigCloudHistoryUploadResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_ConfigurationHistoryUploadFailed")
                };
            }

            if (!CanUseManualCloudActions(config))
            {
                string message = I18n.GetString("CloudSync_Notification_RcloneOnly");
                if (showNotifications)
                {
                    NotificationService.ShowWarning(message, I18n.GetString("CloudSync_Notification_Title"));
                }

                return new ConfigCloudHistoryUploadResult
                {
                    Success = false,
                    Message = message
                };
            }

            if (!TryResolveSharedRcloneRuntime(config.Cloud, out var executablePath, out var workingDirectory, out var errorMessage))
            {
                if (showNotifications)
                {
                    NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                }

                return new ConfigCloudHistoryUploadResult
                {
                    Success = false,
                    Message = errorMessage
                };
            }

            var settings = config.Cloud;
            var localEntries = HistoryService.GetEntriesForConfig(config.Id);
            var manifest = BuildActiveHistoryManifest(config, localEntries);
            string remoteHistoryPath = AppendRemotePath(settings.RemoteBasePath ?? string.Empty, "history.json");
            string activeHistoryRemotePath = BuildActiveHistoryManifestRemotePath(config);

            string tempHistoryPath = Path.Combine(Path.GetTempPath(), $"FolderRewind_config_history_upload_{Guid.NewGuid():N}.json");
            string tempManifestPath = Path.Combine(Path.GetTempPath(), $"FolderRewind_config_active_history_{Guid.NewGuid():N}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(tempHistoryPath) ?? Path.GetTempPath());
            Directory.CreateDirectory(Path.GetDirectoryName(tempManifestPath) ?? Path.GetTempPath());

            var task = CreateTask(I18n.Format("CloudSync_Task_ConfigurationHistoryUploadName", config.Name ?? string.Empty), UploadTaskIconGlyph);
            await RunOnUIAsync(() => BackupService.ActiveTasks.Insert(0, task)).ConfigureAwait(false);

            await CommandSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("CloudSync_Task_Preparing");
                    task.IsIndeterminate = false;
                    task.Progress = 0;
                }).ConfigureAwait(false);

                List<HistoryItem> remoteEntries = await DownloadRemoteHistoryItemsOptionalAsync(
                    executablePath,
                    workingDirectory,
                    settings,
                    remoteHistoryPath,
                    task).ConfigureAwait(false);

                string remoteConfigRoot = AppendRemotePath(settings.RemoteBasePath ?? string.Empty, config.Name ?? string.Empty);
                int removedCount = remoteEntries.RemoveAll(item => BelongsToConfiguration(item, config, remoteConfigRoot));
                remoteEntries.AddRange(localEntries.Select(CloneHistoryItemForCloudSync));

                SerializeToFile(tempHistoryPath, remoteEntries, AppJsonContext.Default.ListHistoryItem);
                SerializeToFile(tempManifestPath, manifest, AppJsonContext.Default.CloudActiveHistoryManifest);

                await RunOnUIAsync(() => task.Progress = 20).ConfigureAwait(false);

                var historyUploadResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(tempHistoryPath, remoteHistoryPath)),
                    I18n.GetString("CloudSync_Task_UploadingConfigurationHistory"),
                    "history.json").ConfigureAwait(false);

                string? warningMessage = null;
                bool success = historyUploadResult.Success;
                string resultMessage;

                if (!historyUploadResult.Success)
                {
                    resultMessage = I18n.Format("CloudSync_Notification_ConfigurationHistoryUploadFailedWithReason", config.Name ?? string.Empty, historyUploadResult.ErrorMessage);
                }
                else
                {
                    await RunOnUIAsync(() => task.Progress = 65).ConfigureAwait(false);

                    var manifestUploadResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(tempManifestPath, activeHistoryRemotePath)),
                        I18n.GetString("CloudSync_Task_UploadingActiveHistoryManifest"),
                        ActiveHistoryManifestFileName).ConfigureAwait(false);

                    if (!manifestUploadResult.Success)
                    {
                        success = true;
                        warningMessage = I18n.Format("CloudSync_Notification_ActiveHistoryManifestUploadFailedWithReason", manifestUploadResult.ErrorMessage);
                        LogService.LogWarning(
                            I18n.Format("CloudSync_Log_CommandFailed", ActiveHistoryManifestFileName, manifestUploadResult.ErrorMessage),
                            nameof(CloudSyncService));
                    }

                    resultMessage = I18n.Format("CloudSync_Notification_ConfigurationHistoryUploadSucceeded", config.Name ?? string.Empty, localEntries.Count);
                }

                if (showNotifications)
                {
                    if (success)
                    {
                        NotificationService.ShowSuccess(resultMessage, I18n.GetString("CloudSync_Notification_Title"));
                    }
                    else
                    {
                        NotificationService.ShowError(resultMessage, I18n.GetString("CloudSync_Notification_Title"));
                    }

                    if (!string.IsNullOrWhiteSpace(warningMessage))
                    {
                        NotificationService.ShowWarning(warningMessage, I18n.GetString("CloudSync_Notification_Title"));
                    }
                }

                await CompleteTaskAsync(
                    task,
                    settings,
                    success,
                    success ? I18n.GetString("CloudSync_Task_Completed") : I18n.GetString("CloudSync_Task_Failed"),
                    success ? warningMessage ?? string.Empty : resultMessage,
                    success ? 0 : historyUploadResult.ExitCode).ConfigureAwait(false);

                return new ConfigCloudHistoryUploadResult
                {
                    Success = success,
                    Message = string.IsNullOrWhiteSpace(warningMessage) ? resultMessage : warningMessage,
                    UploadedEntryCount = localEntries.Count,
                    ReplacedRemoteEntryCount = removedCount
                };
            }
            catch (Exception ex)
            {
                LogService.LogError($"[CloudSyncService] Failed to upload configuration history: {ex.Message}", nameof(CloudSyncService), ex);
                string message = I18n.Format("CloudSync_Notification_ConfigurationHistoryUploadFailedWithReason", config.Name ?? string.Empty, ex.Message);
                if (showNotifications)
                {
                    NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                }

                return new ConfigCloudHistoryUploadResult
                {
                    Success = false,
                    Message = message
                };
            }
            finally
            {
                CommandSemaphore.Release();
                TryDeleteTempFile(tempHistoryPath);
                TryDeleteTempFile(tempManifestPath);
            }
        }

        public static async Task<(bool Success, int DownloadedCount, string Message)> EnsureRestoreChainAvailableAsync(
            BackupConfig? config,
            ManagedFolder? folder,
            HistoryItem? targetItem)
        {
            if (config == null || folder == null || targetItem == null)
            {
                return (false, 0, I18n.GetString("CloudSync_Notification_RestoreChainUnavailable"));
            }

            if (ConfigService.CurrentConfig?.GlobalSettings?.AutoDownloadMissingCloudBackupsBeforeRestore != true
                || !CanUseManualCloudActions(config))
            {
                return (false, 0, I18n.GetString("CloudSync_Notification_RestoreChainUnavailable"));
            }

            var analysis = await AnalyzeConfigurationHistoryCoreAsync(config).ConfigureAwait(false);
            if (!analysis.Success)
            {
                return (false, 0, analysis.Message);
            }

            var importResult = HistoryService.ImportHistoryItems(analysis.MappedItems, merge: true);
            if (!importResult.Success)
            {
                return (false, 0, I18n.GetString("CloudSync_ConfigSync_HistoryImportFailed"));
            }

            var chainItems = BuildRequiredRestoreHistoryChain(config, folder, targetItem, analysis.MappedItems);
            if (chainItems.Count == 0)
            {
                return (false, 0, I18n.GetString("CloudSync_Notification_RestoreChainUnavailable"));
            }

            var missingItems = chainItems
                .Where(item =>
                {
                    string? localPath = HistoryService.GetBackupFilePath(config, folder, item);
                    return string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath);
                })
                .ToList();

            if (missingItems.Count == 0)
            {
                return (true, 0, I18n.GetString("CloudSync_Notification_RestoreChainAlreadyAvailable"));
            }

            var downloadResult = await DownloadHistoryItemsAsync(
                config,
                missingItems,
                I18n.Format("CloudSync_Task_RestoreChainDownloadName", targetItem.FileName)).ConfigureAwait(false);

            if (!downloadResult.Success)
            {
                return downloadResult;
            }

            string message = I18n.Format("CloudSync_Notification_RestoreChainDownloadSucceeded", missingItems.Count, targetItem.FileName);
            NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
            return (true, missingItems.Count, message);
        }

    }
}
