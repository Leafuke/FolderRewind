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
                ConfigService.Save();
            }).ConfigureAwait(false);
        }

        private static async Task<(bool Success, int ExitCode, string ErrorMessage)> ExecuteCommandWithRetryAsync(
            BackupTask task,
            CloudSettings settings,
            ResolvedCommand command,
            string runningStatus,
            string logLabel)
        {
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

                LogService.LogInfo(I18n.Format("CloudSync_Log_CommandStart", logLabel, command.Preview), nameof(CloudSyncService));
                var result = await RunCommandAsync(task, command, timeoutSeconds).ConfigureAwait(false);
                exitCode = result.ExitCode;
                lastError = result.ErrorMessage;

                if (result.Success)
                {
                    LogService.LogInfo(I18n.Format("CloudSync_Log_CommandSucceeded", logLabel), nameof(CloudSyncService));
                    return result;
                }

                LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", logLabel, lastError), nameof(CloudSyncService));
                if (attempt < retryCount)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }

            return (false, exitCode, lastError);
        }

        private static async Task<(bool Success, int ExitCode, string ErrorMessage)> RunCommandAsync(BackupTask task, ResolvedCommand command, int timeoutSeconds)
        {
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

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

                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, args) => AppendLogLine(args.Data, outputBuilder, task, false);
                process.ErrorDataReceived += (_, args) => AppendLogLine(args.Data, errorBuilder, task, true);

                if (!process.Start())
                {
                    return (false, -1, I18n.GetString("CloudSync_Error_StartFailed"));
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

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

                    return (false, -1, I18n.Format("CloudSync_Task_Timeout", timeoutSeconds));
                }

                string errorText = GetBestErrorMessage(process.ExitCode, errorBuilder.ToString(), outputBuilder.ToString());
                return process.ExitCode == 0
                    ? (true, process.ExitCode, string.Empty)
                    : (false, process.ExitCode, errorText);
            }
            catch (Exception ex)
            {
                return (false, -1, ex.Message);
            }
        }

        private static void AppendLogLine(string? line, StringBuilder builder, BackupTask task, bool isError)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;

            }

            builder.AppendLine(line);
            TrimBuilder(builder);

            _ = RunOnUIAsync(() =>
            {
                task.Log = builder.ToString().Trim();
                if (isError)
                {
                    task.ErrorMessage = line;
                }
            });
        }

        private static void TrimBuilder(StringBuilder builder)
        {
            if (builder.Length <= MaxLogLength)
            {
                return;
            }

            builder.Remove(0, builder.Length - MaxLogLength);
        }

        private static string GetBestErrorMessage(int exitCode, string stderr, string stdout)
        {
            var source = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
            if (!string.IsNullOrWhiteSpace(source))
            {
                var sourceSpan = source.AsSpan();
                ReadOnlySpan<char> lastNonEmpty = default;
                foreach (var line in sourceSpan.EnumerateLines())
                {
                    var trimmed = line.Trim();
                    if (!trimmed.IsEmpty)
                        lastNonEmpty = trimmed;
                }
                if (lastNonEmpty.Length > 0)
                    return lastNonEmpty.ToString();
            }

            return I18n.Format("CloudSync_Error_ExitCode", exitCode);
        }

        private static ResolvedCommand ResolveCommand(CloudSettings settings, CloudCommandContext context)
        {
            string executable = settings.CommandMode == CloudCommandMode.Rclone
                ? ResolveRcloneExecutable(settings)
                : settings.ExecutablePath ?? string.Empty;

            string argumentsTemplate = settings.ArgumentsTemplate ?? string.Empty;
            if (settings.CommandMode == CloudCommandMode.Rclone && string.IsNullOrWhiteSpace(argumentsTemplate))
            {
                argumentsTemplate = GetRecommendedArgumentsTemplate(settings.TemplateKind);
            }

            var variables = BuildVariables(context, settings.RemoteBasePath);

            string resolvedExecutable = ReplaceVariables(executable, variables);
            string resolvedArguments = ReplaceVariables(argumentsTemplate, variables);
            string resolvedWorkingDirectory = ReplaceVariables(settings.WorkingDirectory ?? string.Empty, variables);

            (resolvedExecutable, resolvedArguments) = WrapScriptExecutionIfNeeded(resolvedExecutable.Trim(), resolvedArguments.Trim());

            string preview = string.IsNullOrWhiteSpace(resolvedArguments)
                ? resolvedExecutable
                : $"{resolvedExecutable} {resolvedArguments}";

            return new ResolvedCommand
            {
                ExecutablePath = resolvedExecutable.Trim(),
                Arguments = resolvedArguments.Trim(),
                WorkingDirectory = resolvedWorkingDirectory.Trim(),
                Preview = preview.Trim()
            };
        }

        private static ResolvedCommand CreateDirectCommand(string executablePath, string workingDirectory, string arguments)
        {
            string resolvedExecutable = executablePath?.Trim() ?? string.Empty;
            string resolvedArguments = arguments?.Trim() ?? string.Empty;
            (resolvedExecutable, resolvedArguments) = WrapScriptExecutionIfNeeded(resolvedExecutable, resolvedArguments);

            string preview = string.IsNullOrWhiteSpace(resolvedArguments)
                ? resolvedExecutable
                : $"{resolvedExecutable} {resolvedArguments}";

            return new ResolvedCommand
            {
                ExecutablePath = resolvedExecutable,
                Arguments = resolvedArguments,
                WorkingDirectory = workingDirectory ?? string.Empty,
                Preview = preview
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
