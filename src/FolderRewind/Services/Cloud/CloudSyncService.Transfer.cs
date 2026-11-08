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

            if (!TryResolveRcloneRuntime(settings, out var executablePath, out var workingDirectory, out var errorMessage))
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

            if (!TryResolveRcloneRuntime(settings, out var executablePath, out var workingDirectory, out var errorMessage))
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

        private static bool TryResolveRcloneRuntime(
            CloudSettings settings,
            out string executablePath,
            out string workingDirectory,
            out string errorMessage)
        {
            executablePath = ResolveRcloneExecutable(settings);
            workingDirectory = settings.WorkingDirectory.Trim();
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
