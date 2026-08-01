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
        public static async Task<bool> UploadHistoryItemAsync(BackupConfig? config, ManagedFolder? folder, HistoryItem? item)
        {
            if (config == null || folder == null || item == null)
            {
                return false;
            }

            var settings = config.Cloud;
            if (!CanUseManualCloudActions(config))
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_RcloneOnly"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (!TryBuildHistoryCloudPaths(config, folder, item, out var paths, out var errorMessage))
            {
                NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (!File.Exists(paths.ArchiveFilePath))
            {
                NotificationService.ShowWarning(I18n.Format("CloudSync_Log_MissingArchive", paths.ArchiveFilePath), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            var task = CreateTask(I18n.Format("CloudSync_Task_HistoryUploadName", item.FileName), UploadTaskIconGlyph);
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

                string executablePath = ResolveRcloneExecutable(settings);
                string workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
                if (!ValidateExecutableAndWorkingDirectory(executablePath, workingDirectory, out errorMessage))
                {
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), errorMessage, -1).ConfigureAwait(false);
                    NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                    return false;
                }

                var archiveCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.ArchiveFilePath, paths.ArchiveRemotePath));
                var archiveResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    archiveCommand,
                    I18n.GetString("CloudSync_Task_UploadingArchive"),
                    item.FileName).ConfigureAwait(false);

                if (!archiveResult.Success)
                {
                    NotificationService.ShowWarning(
                        I18n.Format("CloudSync_Notification_HistoryUploadFailed", item.FileName, archiveResult.ErrorMessage),
                        I18n.GetString("CloudSync_Notification_Title"));
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), archiveResult.ErrorMessage, archiveResult.ExitCode).ConfigureAwait(false);
                    return false;
                }

                await RunOnUIAsync(() => task.Progress = 40).ConfigureAwait(false);

                string? metadataWarning = null;
                string metadataRecordRemotePath = string.Empty;
                string metadataStateRemotePath = string.Empty;

                if (File.Exists(paths.MetadataStateFilePath))
                {
                    var stateCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.MetadataStateFilePath, paths.MetadataStateRemotePath));
                    var stateResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        stateCommand,
                        I18n.GetString("CloudSync_Task_UploadingMetadata"),
                        item.FileName).ConfigureAwait(false);

                    if (stateResult.Success)
                    {
                        metadataStateRemotePath = paths.MetadataStateRemotePath;
                    }
                    else
                    {
                        metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", item.FileName);
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", item.FileName, stateResult.ErrorMessage), nameof(CloudSyncService));
                    }
                }
                else
                {
                    metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", item.FileName);
                }

                await RunOnUIAsync(() => task.Progress = 70).ConfigureAwait(false);

                if (File.Exists(paths.MetadataRecordFilePath))
                {
                    var recordCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.MetadataRecordFilePath, paths.MetadataRecordRemotePath));
                    var recordResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        recordCommand,
                        I18n.GetString("CloudSync_Task_UploadingMetadata"),
                        item.FileName).ConfigureAwait(false);

                    if (recordResult.Success)
                    {
                        metadataRecordRemotePath = paths.MetadataRecordRemotePath;
                    }
                    else
                    {
                        metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", item.FileName);
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", item.FileName, recordResult.ErrorMessage), nameof(CloudSyncService));
                    }
                }
                else
                {
                    metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", item.FileName);
                }

                HistoryService.UpdateCloudArchiveState(
                    item,
                    true,
                    DateTime.UtcNow,
                    paths.ArchiveRemotePath,
                    metadataRecordRemotePath,
                    metadataStateRemotePath);

                NotificationService.ShowSuccess(
                    I18n.Format("CloudSync_Notification_HistoryUploadSucceeded", item.FileName),
                    I18n.GetString("CloudSync_Notification_Title"));

                if (!string.IsNullOrWhiteSpace(metadataWarning))
                {
                    NotificationService.ShowWarning(metadataWarning, I18n.GetString("CloudSync_Notification_Title"));
                }

                await CompleteTaskAsync(task, settings, true, I18n.GetString("CloudSync_Task_Completed"), string.Empty, 0).ConfigureAwait(false);
                return true;
            }
            finally
            {
                CommandSemaphore.Release();
            }
        }

        public static async Task<bool> DownloadHistoryItemAsync(BackupConfig? config, ManagedFolder? folder, HistoryItem? item)
        {
            if (config == null || folder == null || item == null)
            {
                return false;
            }

            var settings = config.Cloud;
            if (!CanUseManualCloudActions(config))
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_RcloneOnly"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (!item.IsCloudArchived || string.IsNullOrWhiteSpace(item.CloudArchiveRemotePath))
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_NoCloudCopy"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            var result = await DownloadHistoryItemsAsync(
                config,
                [item],
                I18n.Format("CloudSync_Task_HistoryDownloadName", item.FileName)).ConfigureAwait(false);
            return result.Success;
        }

        private static async Task<(bool Success, int DownloadedCount, string Message)> DownloadHistoryItemsAsync(
            BackupConfig config,
            IEnumerable<HistoryItem> items,
            string taskName)
        {
            var settings = config.Cloud;
            var itemList = items?
                .Where(item => item != null)
                .GroupBy(item => $"{item.ConfigId}|{item.FolderPath}|{item.FileName}|{item.Timestamp:O}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList() ?? new List<HistoryItem>();

            if (!CanUseManualCloudActions(config))
            {
                string message = I18n.GetString("CloudSync_Notification_RcloneOnly");
                NotificationService.ShowWarning(message, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, message);
            }

            if (itemList.Count == 0)
            {
                return (true, 0, I18n.GetString("CloudSync_Notification_RestoreChainAlreadyAvailable"));
            }

            string executablePath = ResolveRcloneExecutable(settings);
            string workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
            if (!ValidateExecutableAndWorkingDirectory(executablePath, workingDirectory, out var errorMessage))
            {
                NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, errorMessage);
            }

            var task = CreateTask(taskName, DownloadTaskIconGlyph);
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

                int downloadedCount = 0;
                string lastFailure = string.Empty;

                for (int index = 0; index < itemList.Count; index++)
                {
                    var currentItem = itemList[index];
                    var folder = ResolveFolderForHistoryItem(config, currentItem);
                    if (folder == null)
                    {
                        lastFailure = I18n.GetString("CloudSync_Notification_ConfigurationNoFolders");
                        continue;
                    }

                    if (!TryBuildHistoryCloudPaths(config, folder, currentItem, out var paths, out errorMessage))
                    {
                        lastFailure = errorMessage;
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", currentItem.FileName, errorMessage), nameof(CloudSyncService));
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(paths.ArchiveFilePath)!);
                    if (!string.IsNullOrWhiteSpace(paths.MetadataDir))
                    {
                        Directory.CreateDirectory(paths.MetadataDir);
                    }

                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("CloudSync_Task_DownloadingHistoryItem", currentItem.FileName, index + 1, itemList.Count);
                        task.Progress = itemList.Count == 0 ? 0 : (index * 100.0) / itemList.Count;
                    }).ConfigureAwait(false);

                    var archiveCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.ArchiveRemotePath, paths.ArchiveFilePath));
                    var archiveResult = await ExecuteCommandWithRetryAsync(
                        task,

                        settings,
                        archiveCommand,
                        I18n.GetString("CloudSync_Task_DownloadingArchive"),
                        currentItem.FileName).ConfigureAwait(false);

                    if (!archiveResult.Success)
                    {
                        lastFailure = archiveResult.ErrorMessage;
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", currentItem.FileName, archiveResult.ErrorMessage), nameof(CloudSyncService));
                        continue;
                    }

                    string? metadataWarning = null;
                    if (!string.IsNullOrWhiteSpace(paths.MetadataStateRemotePath))
                    {
                        var stateCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.MetadataStateRemotePath, paths.MetadataStateFilePath));
                        var stateResult = await ExecuteCommandWithRetryAsync(
                            task,
                            settings,
                            stateCommand,
                            I18n.GetString("CloudSync_Task_DownloadingMetadata"),
                            currentItem.FileName).ConfigureAwait(false);

                        if (!stateResult.Success)
                        {
                            metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", currentItem.FileName);
                            LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", currentItem.FileName, stateResult.ErrorMessage), nameof(CloudSyncService));
                        }
                    }
                    else
                    {
                        metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", currentItem.FileName);
                    }

                    if (!string.IsNullOrWhiteSpace(paths.MetadataRecordRemotePath))
                    {
                        var recordCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.MetadataRecordRemotePath, paths.MetadataRecordFilePath));
                        var recordResult = await ExecuteCommandWithRetryAsync(
                            task,
                            settings,
                            recordCommand,
                            I18n.GetString("CloudSync_Task_DownloadingMetadata"),
                            currentItem.FileName).ConfigureAwait(false);

                        if (!recordResult.Success)
                        {
                            metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", currentItem.FileName);
                            LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", currentItem.FileName, recordResult.ErrorMessage), nameof(CloudSyncService));
                        }
                    }
                    else
                    {
                        metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", currentItem.FileName);
                    }

                    HistoryService.UpdateCloudArchiveState(
                        currentItem,
                        true,
                        DateTime.UtcNow,
                        paths.ArchiveRemotePath,
                        paths.MetadataRecordRemotePath,
                        paths.MetadataStateRemotePath);

                    if (!string.IsNullOrWhiteSpace(metadataWarning))
                    {
                        LogService.LogWarning(metadataWarning, nameof(CloudSyncService));
                    }

                    downloadedCount++;
                }

                bool success = downloadedCount > 0;
                string message;
                if (itemList.Count == 1)
                {
                    string fileName = itemList[0].FileName ?? string.Empty;
                    message = success
                        ? I18n.Format("CloudSync_Notification_HistoryDownloadSucceeded", fileName)
                        : string.IsNullOrWhiteSpace(lastFailure)
                            ? I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed")
                            : I18n.Format("CloudSync_Notification_HistoryDownloadFailed", fileName, lastFailure);
                }
                else
                {
                    message = success
                        ? I18n.Format("CloudSync_Notification_ConfigurationDownloadSucceeded", config.Name ?? string.Empty, downloadedCount)
                        : string.IsNullOrWhiteSpace(lastFailure)
                            ? I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed")
                            : I18n.Format("CloudSync_Notification_ConfigurationDownloadFailedWithReason", config.Name ?? string.Empty, lastFailure);
                }

                if (success)
                {
                    NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
                }
                else
                {
                    NotificationService.ShowWarning(message, I18n.GetString("CloudSync_Notification_Title"));
                }

                await CompleteTaskAsync(
                    task,
                    settings,
                    success,
                    success ? I18n.GetString("CloudSync_Task_DownloadCompleted") : I18n.GetString("CloudSync_Task_Failed"),
                    success ? string.Empty : message,
                    success ? 0 : -1).ConfigureAwait(false);

                return (success, downloadedCount, message);
            }
            finally
            {
                CommandSemaphore.Release();
            }
        }

        private static async Task ExecuteConfiguredUploadAsync(BackupConfig config, ManagedFolder folder, CloudSettings settings, CloudCommandContext context)
        {
            var task = CreateTask(I18n.Format("CloudSync_Task_Name", folder.DisplayName), UploadTaskIconGlyph);
            await RunOnUIAsync(() => BackupService.ActiveTasks.Insert(0, task)).ConfigureAwait(false);

            await CommandSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunOnUIAsync(() => task.Status = I18n.GetString("CloudSync_Task_Preparing")).ConfigureAwait(false);

                if (!File.Exists(context.ArchiveFilePath))
                {
                    var message = I18n.Format("CloudSync_Log_MissingArchive", context.ArchiveFilePath);
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), message, -1).ConfigureAwait(false);
                    return;
                }

                var resolved = ResolveCommand(settings, context);
                LogService.LogInfo(I18n.Format("CloudSync_Log_Queued", folder.DisplayName, resolved.Preview), nameof(CloudSyncService));

                if (string.IsNullOrWhiteSpace(resolved.ExecutablePath))
                {
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), I18n.GetString("CloudSync_Error_ExecutableEmpty"), -1).ConfigureAwait(false);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(resolved.WorkingDirectory) && !Directory.Exists(resolved.WorkingDirectory))
                {
                    var message = I18n.Format("CloudSync_Log_WorkingDirectoryMissing", resolved.WorkingDirectory);
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), message, -1).ConfigureAwait(false);
                    return;
                }

                var result = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    resolved,
                    I18n.GetString("CloudSync_Task_Running"),
                    folder.DisplayName).ConfigureAwait(false);

                if (result.Success)
                {
                    string metadataRecordRemotePath = string.Empty;
                    string metadataStateRemotePath = string.Empty;
                    string? metadataWarning = null;

                    if (settings.CommandMode == CloudCommandMode.Rclone)
                    {
                        var metadataResult = await UploadAutomaticMetadataAsync(task, settings, context).ConfigureAwait(false);
                        metadataRecordRemotePath = metadataResult.MetadataRecordRemotePath;
                        metadataStateRemotePath = metadataResult.MetadataStateRemotePath;
                        metadataWarning = metadataResult.WarningMessage;
                    }

                    MarkAutomaticUploadHistoryState(config, folder, settings, context, metadataRecordRemotePath, metadataStateRemotePath);
                    if (settings.SyncHistoryAfterUpload)
                    {
                        var historyUploadResult = await UploadConfigurationHistoryAsync(config, showNotifications: false).ConfigureAwait(false);
                        if (!historyUploadResult.Success)
                        {
                            NotificationService.ShowWarning(historyUploadResult.Message, I18n.GetString("CloudSync_Notification_Title"));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(metadataWarning))
                    {
                        NotificationService.ShowWarning(metadataWarning, I18n.GetString("CloudSync_Notification_Title"));
                    }

                    await CompleteTaskAsync(task, settings, true, I18n.GetString("CloudSync_Task_Completed"), string.Empty, result.ExitCode).ConfigureAwait(false);
                    return;
                }

                NotificationService.ShowWarning(
                    I18n.Format("CloudSync_Notification_UploadFailed", folder.DisplayName, result.ErrorMessage),
                    I18n.GetString("CloudSync_Notification_Title"));
                await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), result.ErrorMessage, result.ExitCode).ConfigureAwait(false);
            }
            finally
            {
                CommandSemaphore.Release();
            }
        }

        private static void MarkAutomaticUploadHistoryState(
            BackupConfig config,
            ManagedFolder folder,
            CloudSettings settings,
            CloudCommandContext context,
            string? metadataRecordRemotePath,
            string? metadataStateRemotePath)
        {
            if (settings.CommandMode != CloudCommandMode.Rclone || settings.TemplateKind == CloudTemplateKind.Custom)
            {
                return;
            }

            var remotePaths = BuildDefaultRemotePaths(context.ConfigName, context.FolderName, context.ArchiveFileName, settings.RemoteBasePath);

            HistoryService.UpdateCloudArchiveState(
                config.Id,
                folder.Path,
                context.ArchiveFileName,
                true,
                DateTime.UtcNow,
                remotePaths.ArchiveRemotePath,
                metadataRecordRemotePath ?? string.Empty,
                metadataStateRemotePath ?? string.Empty);
        }

        private static async Task<string?> UploadHistorySnapshotAfterBackupAsync(BackupTask task, CloudSettings settings)
        {
            if (settings.CommandMode != CloudCommandMode.Rclone)
            {
                return I18n.GetString("CloudSync_Notification_HistorySyncSkippedLegacyMode");
            }

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_history_auto_upload_{Guid.NewGuid():N}.json");
            try
            {
                if (!HistoryService.ExportHistory(tempFilePath) || !File.Exists(tempFilePath))
                {
                    return I18n.GetString("CloudSync_Notification_HistorySyncUploadFailed");
                }

                string remoteHistoryPath = AppendRemotePath(settings.RemoteBasePath ?? string.Empty, "history.json");
                string executablePath = ResolveRcloneExecutable(settings);
                string workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
                if (!ValidateExecutableAndWorkingDirectory(executablePath, workingDirectory, out var errorMessage))
                {
                    return errorMessage;
                }

                await RunOnUIAsync(() => task.Progress = Math.Max(task.Progress, 90)).ConfigureAwait(false);
                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(tempFilePath, remoteHistoryPath));
                var result = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    command,
                    I18n.GetString("CloudSync_Task_UploadingHistorySnapshot"),
                    "history.json").ConfigureAwait(false);

                if (!result.Success)
                {
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", "history.json", result.ErrorMessage), nameof(CloudSyncService));
                    return I18n.Format("CloudSync_Notification_HistorySyncUploadFailedWithReason", result.ErrorMessage);
                }

                return null;
            }
            catch (Exception ex)
            {
                LogService.LogError($"[CloudSyncService] Failed to upload history snapshot automatically: {ex.Message}", nameof(CloudSyncService), ex);
                return I18n.Format("CloudSync_Notification_HistorySyncUploadFailedWithReason", ex.Message);
            }
            finally
            {
                TryDeleteTempFile(tempFilePath);
            }
        }

    }
}
