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
        private static async Task<(bool Success, int Count, string Message)> ImportHistoryFromCloudCoreAsync(
            string remoteHistoryPath,
            bool merge,
            string taskName,
            string failureMessage)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_history_import_{Guid.NewGuid():N}.json");
            var settings = ConfigService.CurrentConfig?.BackupConfigs?.FirstOrDefault()?.Cloud ?? new CloudSettings();

            var downloadResult = await DownloadJsonToTempAsync(remoteHistoryPath, taskName, failureMessage, tempFilePath, settings).ConfigureAwait(false);
            if (!downloadResult.Success)
            {
                TryDeleteTempFile(tempFilePath);
                return (false, 0, downloadResult.Message);
            }

            try
            {
                var importResult = HistoryService.ImportHistory(tempFilePath, merge);
                string message = importResult.Success
                    ? I18n.Format("CloudSync_Notification_HistoryImportSucceeded", importResult.Count)
                    : failureMessage;

                if (importResult.Success)
                {
                    NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
                }
                else
                {
                    NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                }

                return (importResult.Success, importResult.Count, message);
            }
            finally
            {
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static async Task<(bool Success, string Message)> ImportJsonFromCloudAsync(
            string remoteFilePath,
            string taskName,
            string successMessage,
            string failureMessage,
            Func<string, bool> importAction)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_import_{Guid.NewGuid():N}.json");
            var settings = ConfigService.CurrentConfig?.BackupConfigs?.FirstOrDefault()?.Cloud ?? new CloudSettings();

            var downloadResult = await DownloadJsonToTempAsync(remoteFilePath, taskName, failureMessage, tempFilePath, settings).ConfigureAwait(false);
            if (!downloadResult.Success)
            {
                TryDeleteTempFile(tempFilePath);
                return (false, downloadResult.Message);
            }

            try
            {
                bool imported = importAction(tempFilePath);
                string message = imported ? successMessage : failureMessage;

                if (imported)
                {
                    NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
                }
                else
                {
                    NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                }

                return (imported, message);
            }
            finally
            {
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static async Task<(bool Success, string Message)> ExportJsonToCloudAsync(
            string remoteFilePath,
            string taskName,
            string successMessage,
            string failureMessage,
            Func<string, bool> exportAction)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_export_{Guid.NewGuid():N}.json");
            var settings = ConfigService.CurrentConfig?.BackupConfigs?.FirstOrDefault()?.Cloud ?? new CloudSettings();

            try
            {
                bool exported = exportAction(tempFilePath);
                if (!exported || !File.Exists(tempFilePath))
                {
                    NotificationService.ShowError(failureMessage, I18n.GetString("CloudSync_Notification_Title"));
                    return (false, failureMessage);
                }

                var uploadResult = await UploadJsonFromTempAsync(remoteFilePath, taskName, failureMessage, tempFilePath, settings).ConfigureAwait(false);
                if (!uploadResult.Success)
                {
                    return uploadResult;
                }

                NotificationService.ShowSuccess(successMessage, I18n.GetString("CloudSync_Notification_Title"));
                return (true, successMessage);
            }
            finally
            {
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static async Task<(bool Success, string Message)> DownloadJsonToTempAsync(
            string remoteFilePath,
            string taskName,
            string failureMessage,
            string tempFilePath,
            CloudSettings settings)
        {
            if (string.IsNullOrWhiteSpace(remoteFilePath))
            {
                return (false, failureMessage);
            }

            if (!TryResolveSharedRcloneRuntime(settings, out var executablePath, out var workingDirectory, out var errorMessage))
            {
                NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                return (false, errorMessage);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath) ?? Path.GetTempPath());

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

                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(remoteFilePath, tempFilePath));
                var result = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    command,
                    I18n.GetString("CloudSync_Task_DownloadingArchive"),
                    taskName).ConfigureAwait(false);

                if (!result.Success)
                {
                    string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? failureMessage
                        : I18n.Format("CloudSync_Notification_ImportFailedWithReason", result.ErrorMessage);
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), message, result.ExitCode).ConfigureAwait(false);
                    NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                    return (false, message);
                }

                await CompleteTaskAsync(task, settings, true, I18n.GetString("CloudSync_Task_DownloadCompleted"), string.Empty, result.ExitCode).ConfigureAwait(false);
                return (true, string.Empty);
            }
            finally
            {
                CommandSemaphore.Release();
            }
        }

        private static async Task<(bool Success, string Message)> UploadJsonFromTempAsync(
            string remoteFilePath,
            string taskName,
            string failureMessage,
            string tempFilePath,
            CloudSettings settings)
        {
            if (string.IsNullOrWhiteSpace(remoteFilePath) || string.IsNullOrWhiteSpace(tempFilePath) || !File.Exists(tempFilePath))
            {
                return (false, failureMessage);
            }

            if (!TryResolveSharedRcloneRuntime(settings, out var executablePath, out var workingDirectory, out var errorMessage))
            {
                NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                return (false, errorMessage);
            }

            var task = CreateTask(taskName, UploadTaskIconGlyph);
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

                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(tempFilePath, remoteFilePath));
                var result = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    command,
                    I18n.GetString("CloudSync_Task_UploadingArchive"),
                    taskName).ConfigureAwait(false);

                if (!result.Success)
                {
                    string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? failureMessage
                        : I18n.Format("CloudSync_Notification_ExportFailedWithReason", result.ErrorMessage);
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), message, result.ExitCode).ConfigureAwait(false);
                    NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                    return (false, message);
                }

                await CompleteTaskAsync(task, settings, true, I18n.GetString("CloudSync_Task_Completed"), string.Empty, result.ExitCode).ConfigureAwait(false);
                return (true, string.Empty);
            }
            finally
            {
                CommandSemaphore.Release();
            }
        }

        private static async Task<(string MetadataRecordRemotePath, string MetadataStateRemotePath, string? WarningMessage)> UploadAutomaticMetadataAsync(

            BackupTask task,
            CloudSettings settings,
            CloudCommandContext context)
        {
            if (settings.CommandMode != CloudCommandMode.Rclone || settings.TemplateKind == CloudTemplateKind.Custom)
            {
                return (string.Empty, string.Empty, null);
            }

            string executablePath = ResolveRcloneExecutable(settings);
            string workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
            var remotePaths = BuildDefaultRemotePaths(context.ConfigName, context.FolderName, context.ArchiveFileName, settings.RemoteBasePath);

            string metadataStateRemotePath = string.Empty;
            string metadataRecordRemotePath = string.Empty;
            string? metadataWarning = null;

            if (BackupMetadataStoreService.TryGetStateFilePath(context.MetadataDir, out var stateFilePath) && File.Exists(stateFilePath))
            {
                await RunOnUIAsync(() => task.Progress = 70).ConfigureAwait(false);
                var stateCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(stateFilePath, remotePaths.MetadataStateRemotePath));
                var stateResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    stateCommand,
                    I18n.GetString("CloudSync_Task_UploadingMetadata"),
                    context.FolderName).ConfigureAwait(false);

                if (stateResult.Success)
                {
                    metadataStateRemotePath = remotePaths.MetadataStateRemotePath;
                }
                else
                {
                    metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", context.ArchiveFileName);
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", context.ArchiveFileName, stateResult.ErrorMessage), nameof(CloudSyncService));
                }
            }
            else
            {
                metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", context.ArchiveFileName);
            }

            if (BackupMetadataStoreService.TryGetRecordFilePath(context.MetadataDir, context.ArchiveFileName, out var recordFilePath) && File.Exists(recordFilePath))
            {
                await RunOnUIAsync(() => task.Progress = 85).ConfigureAwait(false);
                var recordCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(recordFilePath, remotePaths.MetadataRecordRemotePath));
                var recordResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    recordCommand,
                    I18n.GetString("CloudSync_Task_UploadingMetadata"),
                    context.FolderName).ConfigureAwait(false);

                if (recordResult.Success)
                {
                    metadataRecordRemotePath = remotePaths.MetadataRecordRemotePath;
                }
                else
                {
                    metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", context.ArchiveFileName);
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", context.ArchiveFileName, recordResult.ErrorMessage), nameof(CloudSyncService));
                }
            }
            else
            {
                metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", context.ArchiveFileName);
            }

            return (metadataRecordRemotePath, metadataStateRemotePath, metadataWarning);
        }

        private static bool TryResolveSharedRcloneRuntime(
            CloudSettings? fallbackSettings,
            out string executablePath,
            out string workingDirectory,
            out string errorMessage)
        {
            // 运行时优先级：全局设置 -> 已配置任务中可复用值 -> 当前回退设置。
            executablePath = ConfigService.CurrentConfig?.GlobalSettings?.RcloneExecutablePath?.Trim() ?? string.Empty;
            workingDirectory = string.Empty;

            var configs = ConfigService.CurrentConfig?.BackupConfigs ?? Enumerable.Empty<BackupConfig>();
            foreach (var config in configs)
            {
                if (config?.Cloud == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(workingDirectory) && !string.IsNullOrWhiteSpace(config.Cloud.WorkingDirectory))
                {
                    workingDirectory = config.Cloud.WorkingDirectory.Trim();
                }

                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    string candidate = config.Cloud.ExecutablePath?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(candidate) && !string.Equals(candidate, DefaultRcloneExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        executablePath = candidate;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = ResolveRcloneExecutable(fallbackSettings);
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = fallbackSettings?.WorkingDirectory?.Trim() ?? string.Empty;
            }

            return ValidateExecutableAndWorkingDirectory(executablePath, workingDirectory, out errorMessage);
        }

        private static async Task<List<string>> ListRemoteFilesAsync(
            string executablePath,
            string workingDirectory,
            CloudSettings settings,
            string remoteFolderRoot)
        {
            var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneListFileArguments(remoteFolderRoot));
            var result = await RunSilentCommandAsync(command, Math.Clamp(settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                return new List<string>();
            }

            var outputSpan = result.Output.AsSpan();
            var lines = new List<string>();
            foreach (var line in outputSpan.EnumerateLines())
            {
                var trimmed = line.Trim();
                if (!trimmed.IsEmpty)
                    lines.Add(trimmed.ToString());
            }
            return lines;
        }

        private static async Task<(bool Success, int ExitCode, string Output, string ErrorMessage)> RunSilentCommandAsync(ResolvedCommand command, int timeoutSeconds)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = command.ExecutablePath,
                    Arguments = command.Arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
                {
                    startInfo.WorkingDirectory = command.WorkingDirectory;
                }

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return (false, -1, string.Empty, I18n.GetString("CloudSync_Error_StartFailed"));
                }

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }

                    return (false, -1, string.Empty, I18n.Format("CloudSync_Task_Timeout", timeoutSeconds));
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                string errorMessage = GetBestErrorMessage(process.ExitCode, error, output);
                return (process.ExitCode == 0, process.ExitCode, output, errorMessage);
            }
            catch (Exception ex)
            {
                return (false, -1, string.Empty, ex.Message);
            }
        }

    }
}
