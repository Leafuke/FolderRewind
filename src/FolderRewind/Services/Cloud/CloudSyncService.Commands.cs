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
        private static BackupTask CreateTask(string folderName, string iconGlyph)
        {
            return new BackupTask
            {
                FolderName = folderName,
                Status = I18n.GetString("CloudSync_Task_Queued"),
                Progress = 0,
                IsIndeterminate = true,
                IconGlyph = iconGlyph
            };
        }

        private static async Task CompleteTaskAsync(BackupTask task, CloudSettings settings, bool success, string status, string errorMessage, int finalExitCode)
        {
            await RunOnUIAsync(() =>
            {
                task.Status = status;
                task.Progress = success ? 100 : task.Progress;
                task.IsCompleted = true;
                task.IsIndeterminate = false;
                task.IsSuccess = success;
                task.ErrorMessage = errorMessage ?? string.Empty;

                settings.LastRunUtc = DateTime.UtcNow;
                settings.LastExitCode = finalExitCode;
                settings.LastErrorMessage = success ? string.Empty : (errorMessage ?? string.Empty);
            }).ConfigureAwait(false);

            var saveResult = await ConfigService.SaveAsync().ConfigureAwait(false);
            if (!saveResult.Success)
            {
                LogService.LogWarning(
                    $"Failed to persist cloud task state: {saveResult.ErrorMessage}",
                    nameof(CloudSyncService));
            }
        }

        private static async Task<(bool Success, int ExitCode, string ErrorMessage)> ExecuteCommandWithRetryAsync(
            BackupTask task,
            CloudSettings settings,
            ResolvedCommand command,
            string runningStatus,
            string logLabel)
        {
            using var operation = NotificationOperationScope.Current is null
                ? new NotificationOperationScope(new(NotificationTargetKind.CloudProject), context: new("", "Cloud")) : null;
            using var diagnostics = new BusinessOperationDiagnostics("CloudCommandRetry", operation is null);
            int retryCount = Math.Clamp(settings.RetryCount, 0, MaxRetryCount);
            int timeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 10, MaxTimeoutSeconds);
            int exitCode = -1;
            string lastError = string.Empty;

            for (int attempt = 0; attempt <= retryCount; attempt++)
            {
                bool isRetry = attempt > 0;
                await RunOnUIAsync(() =>
                {
                    task.Status = isRetry
                        ? I18n.Format("CloudSync_Task_Retrying", attempt + 1, retryCount + 1)
                        : runningStatus;
                    task.ErrorMessage = string.Empty;
                }).ConfigureAwait(false);

                LogService.LogInfo(
                    I18n.Format("CloudSync_Log_CommandStart", command.LogPreview),
                    nameof(CloudSyncService));
                var result = await RunCommandAsync(task, command, timeoutSeconds).ConfigureAwait(false);
                exitCode = result.ExitCode;
                lastError = CloudCommandSecurity.Redact(result.ErrorMessage);

                if (result.Success)
                {
                    diagnostics.Complete("Success", detail: $"commandAttempts={attempt + 1}; exitCode={result.ExitCode}");
                    return result;
                }

                LogService.LogWarning($"event=cloud.command-attempt; attempt={attempt + 1}; exitCode={exitCode}; retrying={attempt < retryCount}", nameof(CloudSyncService));
                if (attempt < retryCount)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }

            diagnostics.Complete("Failed", LogLevel.Error, $"commandAttempts={retryCount + 1}; exitCode={exitCode}");
            return (false, exitCode, lastError);
        }

        private static async Task<(bool Success, int ExitCode, string ErrorMessage)> RunCommandAsync(BackupTask task, ResolvedCommand command, int timeoutSeconds)
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
                _ = await RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(timeoutSeconds), default).ConfigureAwait(false);
                await RunOnUIAsync(() => task.Log = I18n.GetString("CloudSync_OutputOmitted")).ConfigureAwait(false);
                return (true, 0, string.Empty);
            }
            catch (RcloneProcessException ex) { return (false, ex.ExitCode, CloudCommandSecurity.Redact(ex.Message)); }
            catch (Exception ex)
            {
                RcloneCredentialDiagnosticReporter.Report(ex, nameof(CloudSyncService));
                return (false, -1, CloudCommandSecurity.Redact(ex.Message));
            }
        }

        private static ResolvedCommand CreateDirectCommand(string executablePath, string workingDirectory, string arguments)
        {
            string resolvedExecutable = executablePath?.Trim() ?? string.Empty;
            string resolvedArguments = arguments?.Trim() ?? string.Empty;
            string operationCategory = CloudCommandSecurity.DetectOperationCategory(resolvedArguments);
            (resolvedExecutable, resolvedArguments) = WrapScriptExecutionIfNeeded(resolvedExecutable, resolvedArguments);
            var previews = CloudCommandSecurity.BuildPreviews(
                resolvedExecutable,
                resolvedArguments,
                operationCategory);

            return new ResolvedCommand
            {
                ExecutablePath = resolvedExecutable,
                Arguments = resolvedArguments,
                WorkingDirectory = workingDirectory ?? string.Empty,
                DisplayPreview = previews.DisplayPreview,
                LogPreview = previews.LogPreview
            };
        }

        private static string ResolveRcloneExecutable(CloudSettings? settings)
        {
            string configExecutable = settings?.ExecutablePath?.Trim() ?? string.Empty;
            string globalExecutable = ConfigService.CurrentConfig?.GlobalSettings?.RcloneExecutablePath?.Trim() ?? string.Empty;

            bool hasConfigOverride = !string.IsNullOrWhiteSpace(configExecutable)
                && !string.Equals(configExecutable, DefaultRcloneExecutable, StringComparison.OrdinalIgnoreCase);

            if (hasConfigOverride)
            {
                return configExecutable;
            }

            if (!string.IsNullOrWhiteSpace(globalExecutable))
            {
                return globalExecutable;
            }

            if (!string.IsNullOrWhiteSpace(configExecutable))
            {
                return configExecutable;
            }

            return DefaultRcloneExecutable;
        }

        private static bool ValidateExecutableAndWorkingDirectory(string executablePath, string workingDirectory, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                errorMessage = I18n.GetString("CloudSync_Error_ExecutableEmpty");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
            {
                errorMessage = I18n.Format("CloudSync_Log_WorkingDirectoryMissing", workingDirectory);
                return false;
            }

            return true;
        }

    }
}
