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
        private static async Task<(bool Success, string Message)> ImportJsonFromCloudAsync(
            string remoteFilePath,
            string taskName,
            string successMessage,
            string failureMessage,
            Func<string, Task<ConfigImportResult>> importAction,
            CloudSettings settings)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_import_{Guid.NewGuid():N}.json");

            var downloadResult = await DownloadJsonToTempAsync(remoteFilePath, taskName, failureMessage, tempFilePath, settings).ConfigureAwait(false);
            if (!downloadResult.Success)
            {
                TryDeleteTempFile(tempFilePath);
                return (false, downloadResult.Message);
            }

            try
            {
                var result = await importAction(tempFilePath).ConfigureAwait(false);
                bool imported = result.Success;
                string message = imported ? I18n.GetString("Migration_ConfigPrepared")
                    : result.Status == ConfigImportStatus.Canceled ? I18n.GetString("Common_Cancel") : failureMessage;

                if (imported)
                {
                    NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
                }
                else if (result.Status != ConfigImportStatus.Canceled)
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
            Func<string, bool> exportAction,
            CloudSettings settings)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_export_{Guid.NewGuid():N}.json");

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

        private static bool TryResolveSharedRcloneRuntime(
            CloudSettings? fallbackSettings,
            out string executablePath,
            out string workingDirectory,
            out string errorMessage)
        {
            if (!string.IsNullOrWhiteSpace(fallbackSettings?.RcloneConfigPath))
            {
                // 新连接只解析本次选择和主机工具缺省，不借用其他项目的程序或工作目录。
                executablePath = ResolveRcloneExecutable(fallbackSettings);
                workingDirectory = fallbackSettings.WorkingDirectory.Trim();
                return ValidateExecutableAndWorkingDirectory(executablePath, workingDirectory, out errorMessage);
            }
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



        private static async Task<(bool Success, int ExitCode, string Output, string ErrorMessage)> RunSilentCommandAsync(ResolvedCommand command, int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            try
            {
                var start = command.Execution is { } context
                    ? context.CreateStartInfo(ProcessArgumentTokenizer.Parse(command.Arguments))
                    : new ProcessStartInfo(command.ExecutablePath, command.Arguments)
                    {
                        WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                var output = await RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
                return (true, 0, output, string.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (RcloneProcessException ex) { return (false, ex.ExitCode, string.Empty, CloudCommandSecurity.Redact(ex.Message)); }
            catch (Exception ex) { return (false, -1, string.Empty, CloudCommandSecurity.Redact(ex.Message)); }
        }
    }
}
