using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class CloudSyncService
    {
        private const string DefaultRcloneExecutable = "rclone.exe";
        private const string DefaultWindowsPowerShellExecutable = "powershell.exe";
        private const string DefaultCommandShellExecutable = "cmd.exe";
        private const string UploadTaskIconGlyph = "\uE896";
        private const int MaxRetryCount = 5;
        private const int MaxTimeoutSeconds = 86400;
        private const int MaxLogLength = 4096;
        // 串行上传：避免多个外部进程同时跑导致带宽争抢、日志混杂和远端限流。
        private static readonly SemaphoreSlim UploadSemaphore = new(1, 1);

        private sealed class CloudCommandContext
        {
            public required string ConfigName { get; init; }
            public required string ConfigId { get; init; }
            public required string FolderName { get; init; }
            public required string SourcePath { get; init; }
            public required string DestinationPath { get; init; }
            public required string BackupSubDir { get; init; }
            public required string MetadataDir { get; init; }
            public required string ArchiveFileName { get; init; }
            public required string ArchiveFilePath { get; init; }
            public required string BackupMode { get; init; }
            public required string Comment { get; init; }
            public required string Timestamp { get; init; }
        }

        private sealed class ResolvedCommand
        {
            public required string ExecutablePath { get; init; }
            public required string Arguments { get; init; }
            public required string WorkingDirectory { get; init; }
            public required string Preview { get; init; }
        }

        public static string VariablesHelpText => string.Join(Environment.NewLine, new[]
        {
            "{ArchiveFilePath}",
            "{ArchiveFileName}",
            "{BackupSubDir}",
            "{MetadataDir}",
            "{ConfigName}",
            "{ConfigId}",
            "{FolderName}",
            "{SourcePath}",
            "{DestinationPath}",
            "{BackupMode}",
            "{Comment}",
            "{Timestamp}",
            "{RemoteBasePath}"
        });

        public static void ApplyRecommendedTemplate(CloudSettings? settings)
        {
            if (settings == null) return;

            if (string.IsNullOrWhiteSpace(settings.ExecutablePath))
                settings.ExecutablePath = DefaultRcloneExecutable;

            if (string.IsNullOrWhiteSpace(settings.RemoteBasePath))
                settings.RemoteBasePath = "remote:FolderRewind";

            settings.ArgumentsTemplate = GetRecommendedArgumentsTemplate(settings.TemplateKind);
        }

        public static string BuildPreview(BackupConfig? config)
        {
            if (config?.Cloud == null)
                return string.Empty;

            var context = BuildSampleContext(config);
            var resolved = ResolveCommand(config.Cloud, context);
            return resolved.Preview;
        }

        public static void QueueUploadAfterBackup(BackupConfig? config, ManagedFolder? folder, string? archiveFileName, string? comment)
        {
            if (config?.Cloud?.Enabled != true || folder == null || string.IsNullOrWhiteSpace(archiveFileName))
                return;

            var context = BuildRuntimeContext(config, folder, archiveFileName!, comment);
            var settings = config.Cloud;

            _ = Task.Run(async () =>
            {
                try
                {
                    // 这里刻意采用后台异步提交，不阻塞主备份流程。
                    await ExecuteUploadAsync(config, folder, settings, context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName, ex.Message), nameof(CloudSyncService));
                }
            });
        }

        private static CloudCommandContext BuildRuntimeContext(BackupConfig config, ManagedFolder folder, string archiveFileName, string? comment)
        {
            string backupSubDir = Path.Combine(config.DestinationPath ?? string.Empty, folder.DisplayName ?? string.Empty);
            string metadataDir = Path.Combine(config.DestinationPath ?? string.Empty, "_metadata", folder.DisplayName ?? string.Empty);
            string archiveFilePath = Path.Combine(backupSubDir, archiveFileName);

            return new CloudCommandContext
            {
                ConfigName = config.Name ?? string.Empty,
                ConfigId = config.Id ?? string.Empty,
                FolderName = folder.DisplayName ?? string.Empty,
                SourcePath = folder.Path ?? string.Empty,
                DestinationPath = config.DestinationPath ?? string.Empty,
                BackupSubDir = backupSubDir,
                MetadataDir = metadataDir,
                ArchiveFileName = archiveFileName,
                ArchiveFilePath = archiveFilePath,
                BackupMode = config.Archive?.Mode.ToString() ?? BackupMode.Full.ToString(),
                Comment = comment ?? string.Empty,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            };
        }

        private static CloudCommandContext BuildSampleContext(BackupConfig config)
        {
            var sampleFolder = config.SourceFolders.FirstOrDefault();
            string folderName = !string.IsNullOrWhiteSpace(sampleFolder?.DisplayName) ? sampleFolder.DisplayName : "SampleFolder";
            string sourcePath = !string.IsNullOrWhiteSpace(sampleFolder?.Path) ? sampleFolder.Path : @"C:\Data\SampleFolder";
            string destinationPath = !string.IsNullOrWhiteSpace(config.DestinationPath) ? config.DestinationPath : @"D:\FolderRewind-Backup";
            string format = string.IsNullOrWhiteSpace(config.Archive?.Format) ? "7z" : config.Archive.Format;
            string archiveFileName = $"[Full][{DateTime.Now:yyyy-MM-dd_HH-mm-ss}]Sample.{format}";
            string backupSubDir = Path.Combine(destinationPath, folderName);

            return new CloudCommandContext
            {
                ConfigName = string.IsNullOrWhiteSpace(config.Name) ? "DefaultConfig" : config.Name,
                ConfigId = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString() : config.Id,
                FolderName = folderName,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                BackupSubDir = backupSubDir,
                MetadataDir = Path.Combine(destinationPath, "_metadata", folderName),
                ArchiveFileName = archiveFileName,
                ArchiveFilePath = Path.Combine(backupSubDir, archiveFileName),
                BackupMode = config.Archive?.Mode.ToString() ?? BackupMode.Full.ToString(),
                Comment = "ManualBackup",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            };
        }

        private static async Task ExecuteUploadAsync(BackupConfig config, ManagedFolder folder, CloudSettings settings, CloudCommandContext context)
        {
            var task = new BackupTask
            {
                FolderName = I18n.Format("CloudSync_Task_Name", folder.DisplayName),
                Status = I18n.GetString("CloudSync_Task_Queued"),
                Progress = 0,
                IsIndeterminate = true,
                IconGlyph = UploadTaskIconGlyph
            };

            await RunOnUIAsync(() => BackupService.ActiveTasks.Insert(0, task)).ConfigureAwait(false);

            // 全局串行门闩，确保同一时刻只执行一个上传任务。
            await UploadSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                await RunOnUIAsync(() => task.Status = I18n.GetString("CloudSync_Task_Preparing")).ConfigureAwait(false);

                if (!File.Exists(context.ArchiveFilePath))
                {
                    var message = I18n.Format("CloudSync_Log_MissingArchive", context.ArchiveFilePath);
                    await CompleteTaskAsync(task, false, I18n.GetString("CloudSync_Task_Failed"), message, -1).ConfigureAwait(false);
                    return;
                }

                var resolved = ResolveCommand(settings, context);
                LogService.LogInfo(I18n.Format("CloudSync_Log_Queued", folder.DisplayName, resolved.Preview), nameof(CloudSyncService));

                if (string.IsNullOrWhiteSpace(resolved.ExecutablePath))
                {
                    await CompleteTaskAsync(task, false, I18n.GetString("CloudSync_Task_Failed"), I18n.GetString("CloudSync_Error_ExecutableEmpty"), -1).ConfigureAwait(false);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(resolved.WorkingDirectory) && !Directory.Exists(resolved.WorkingDirectory))
                {
                    var message = I18n.Format("CloudSync_Log_WorkingDirectoryMissing", resolved.WorkingDirectory);
                    await CompleteTaskAsync(task, false, I18n.GetString("CloudSync_Task_Failed"), message, -1).ConfigureAwait(false);
                    return;
                }

                int retryCount = Math.Clamp(settings.RetryCount, 0, MaxRetryCount);
                int timeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 10, MaxTimeoutSeconds);
                int exitCode = -1;
                string lastError = string.Empty;

                // 第 0 次就是首试，后续才是重试。
                for (int attempt = 0; attempt <= retryCount; attempt++)
                {
                    bool isRetry = attempt > 0;
                    await RunOnUIAsync(() =>
                    {
                        task.Status = isRetry
                            ? I18n.Format("CloudSync_Task_Retrying", attempt + 1, retryCount + 1)
                            : I18n.GetString("CloudSync_Task_Running");
                        task.ErrorMessage = string.Empty;
                    }).ConfigureAwait(false);

                    LogService.LogInfo(I18n.Format("CloudSync_Log_CommandStart", folder.DisplayName, resolved.Preview), nameof(CloudSyncService));

                    var result = await RunCommandAsync(task, resolved, timeoutSeconds).ConfigureAwait(false);
                    exitCode = result.ExitCode;
                    lastError = result.ErrorMessage;

                    if (result.Success)
                    {
                        LogService.LogInfo(I18n.Format("CloudSync_Log_CommandSucceeded", folder.DisplayName), nameof(CloudSyncService));
                        await CompleteTaskAsync(task, true, I18n.GetString("CloudSync_Task_Completed"), string.Empty, exitCode).ConfigureAwait(false);
                        return;
                    }

                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName, lastError), nameof(CloudSyncService));

                    if (attempt < retryCount)
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                }

                NotificationService.ShowWarning(
                    I18n.Format("CloudSync_Notification_UploadFailed", folder.DisplayName, lastError),
                    I18n.GetString("CloudSync_Notification_Title"));

                await CompleteTaskAsync(task, false, I18n.GetString("CloudSync_Task_Failed"), lastError, exitCode).ConfigureAwait(false);
            }
            finally
            {
                // 无论成功失败都必须释放，否则后续上传会被永久卡住。
                UploadSemaphore.Release();
            }

            async Task CompleteTaskAsync(BackupTask backupTask, bool success, string status, string errorMessage, int finalExitCode)
            {
                await RunOnUIAsync(() =>
                {
                    backupTask.Status = status;
                    backupTask.Progress = success ? 100 : 0;
                    backupTask.IsCompleted = true;
                    backupTask.IsIndeterminate = false;
                    backupTask.IsSuccess = success;
                    backupTask.ErrorMessage = errorMessage ?? string.Empty;

                    settings.LastRunUtc = DateTime.UtcNow;
                    settings.LastExitCode = finalExitCode;
                    settings.LastErrorMessage = success ? string.Empty : (errorMessage ?? string.Empty);
                    ConfigService.Save();
                }).ConfigureAwait(false);
            }
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
                    startInfo.WorkingDirectory = command.WorkingDirectory;

                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                process.OutputDataReceived += (_, args) => AppendLogLine(args.Data, outputBuilder, task, false);
                process.ErrorDataReceived += (_, args) => AppendLogLine(args.Data, errorBuilder, task, true);

                if (!process.Start())
                    return (false, -1, I18n.GetString("CloudSync_Error_StartFailed"));

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
                            // 超时直接杀进程树，避免外部工具残留子进程持续占用资源。
                            process.Kill(entireProcessTree: true);
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
                return;

            builder.AppendLine(line);
            TrimBuilder(builder);

            _ = RunOnUIAsync(() =>
            {
                task.Log = builder.ToString().Trim();
                if (isError)
                    task.ErrorMessage = line;
            });
        }

        private static void TrimBuilder(StringBuilder builder)
        {
            if (builder.Length <= MaxLogLength)
                return;

            builder.Remove(0, builder.Length - MaxLogLength);
        }

        private static string GetBestErrorMessage(int exitCode, string stderr, string stdout)
        {
            var source = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
            if (!string.IsNullOrWhiteSpace(source))
            {
                var lines = source
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();

                if (lines.Length > 0)
                    return lines[^1];
            }

            return I18n.Format("CloudSync_Error_ExitCode", exitCode);
        }

        private static ResolvedCommand ResolveCommand(CloudSettings settings, CloudCommandContext context)
        {
            string executable = settings.CommandMode == CloudCommandMode.Rclone && string.IsNullOrWhiteSpace(settings.ExecutablePath)
                ? DefaultRcloneExecutable
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

        private static (string Executable, string Arguments) WrapScriptExecutionIfNeeded(string executablePath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return (string.Empty, arguments);

            string extension = Path.GetExtension(executablePath);
            if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                string combined = $"-ExecutionPolicy Bypass -File {Quote(executablePath)}";
                if (!string.IsNullOrWhiteSpace(arguments))
                    combined += " " + arguments;

                return (DefaultWindowsPowerShellExecutable, combined);
            }

            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                string combined = $"/c \"{executablePath}";
                if (!string.IsNullOrWhiteSpace(arguments))
                    combined += " " + arguments;
                combined += "\"";

                return (DefaultCommandShellExecutable, combined);
            }

            return (executablePath, arguments);
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "\"\"";

            return value.StartsWith('"') && value.EndsWith('"') ? value : $"\"{value}\"";
        }

        private static Dictionary<string, string> BuildVariables(CloudCommandContext context, string? remoteBasePath)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ArchiveFilePath"] = context.ArchiveFilePath,
                ["ArchiveFileName"] = context.ArchiveFileName,
                ["BackupSubDir"] = context.BackupSubDir,
                ["MetadataDir"] = context.MetadataDir,
                ["ConfigName"] = context.ConfigName,
                ["ConfigId"] = context.ConfigId,
                ["FolderName"] = context.FolderName,
                ["SourcePath"] = context.SourcePath,
                ["DestinationPath"] = context.DestinationPath,
                ["BackupMode"] = context.BackupMode,
                ["Comment"] = context.Comment,
                ["Timestamp"] = context.Timestamp,
                ["RemoteBasePath"] = string.IsNullOrWhiteSpace(remoteBasePath) ? "remote:FolderRewind" : remoteBasePath.Trim().TrimEnd('/')
            };
        }

        private static string ReplaceVariables(string input, IReadOnlyDictionary<string, string> variables)
        {
            if (string.IsNullOrWhiteSpace(input) || variables.Count == 0)
                return input ?? string.Empty;

            string result = input;
            foreach (var pair in variables)
            {
                result = Regex.Replace(
                    result,
                    Regex.Escape("{" + pair.Key + "}"),
                    _ => pair.Value ?? string.Empty,
                    RegexOptions.IgnoreCase);
            }

            return result;
        }

        private static string GetRecommendedArgumentsTemplate(CloudTemplateKind templateKind)
        {
            return templateKind switch
            {
                CloudTemplateKind.UploadBackupDirectory => "copy \"{BackupSubDir}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}\"",
                CloudTemplateKind.Custom => string.Empty,
                _ => "copyto \"{ArchiveFilePath}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}/{ArchiveFileName}\""
            };
        }

        private static Task RunOnUIAsync(Action action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }
    }
}