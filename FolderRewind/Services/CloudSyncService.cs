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
        private const string DownloadTaskIconGlyph = "\uE898";
        private const int MaxRetryCount = 5;
        private const int MaxTimeoutSeconds = 86400;
        private const int MaxLogLength = 4096;
        private static readonly SemaphoreSlim CommandSemaphore = new(1, 1);

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

        private sealed class HistoryCloudPaths
        {
            public required string FolderName { get; init; }
            public required string ArchiveFilePath { get; init; }
            public required string ArchiveRemotePath { get; init; }
            public required string MetadataDir { get; init; }
            public required string MetadataStateFilePath { get; init; }
            public required string MetadataRecordFilePath { get; init; }
            public required string MetadataStateRemotePath { get; init; }
            public required string MetadataRecordRemotePath { get; init; }
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
            if (settings == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.ExecutablePath))
            {
                settings.ExecutablePath = DefaultRcloneExecutable;
            }

            if (string.IsNullOrWhiteSpace(settings.RemoteBasePath))
            {
                settings.RemoteBasePath = "remote:FolderRewind";
            }

            settings.ArgumentsTemplate = GetRecommendedArgumentsTemplate(settings.TemplateKind);
        }

        public static string BuildPreview(BackupConfig? config)
        {
            if (config?.Cloud == null)
            {
                return string.Empty;
            }

            var context = BuildSampleContext(config);
            var resolved = ResolveCommand(config.Cloud, context);
            return resolved.Preview;
        }

        public static bool CanUseHistoryCloudActions(BackupConfig? config)
        {
            return config?.Cloud?.Enabled == true
                && config.Cloud.CommandMode == CloudCommandMode.Rclone;
        }

        public static void QueueUploadAfterBackup(BackupConfig? config, ManagedFolder? folder, string? archiveFileName, string? comment)
        {
            if (config?.Cloud?.Enabled != true || folder == null || string.IsNullOrWhiteSpace(archiveFileName))
            {
                return;
            }

            var context = BuildRuntimeContext(config, folder, archiveFileName!, comment);
            var settings = config.Cloud;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteConfiguredUploadAsync(config, folder, settings, context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName, ex.Message), nameof(CloudSyncService));
                }
            });
        }

        private static CloudCommandContext BuildRuntimeContext(BackupConfig config, ManagedFolder folder, string archiveFileName, string? comment)
        {
            string destinationPath = config.DestinationPath ?? string.Empty;
            string backupSubDir = Path.Combine(destinationPath, folder.DisplayName ?? string.Empty);
            string metadataDir = Path.Combine(destinationPath, "_metadata", folder.DisplayName ?? string.Empty);
            if (BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationPath,
                folder.DisplayName ?? string.Empty,
                folder.Path,
                out _,
                out var resolvedBackupSubDir,
                out var resolvedMetadataDir))
            {
                backupSubDir = resolvedBackupSubDir;
                metadataDir = resolvedMetadataDir;
            }

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
                ArchiveFilePath = Path.Combine(backupSubDir, archiveFileName),
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
            string metadataDir = Path.Combine(destinationPath, "_metadata", folderName);
            if (BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationPath,
                folderName,
                sourcePath,
                out _,
                out var resolvedBackupSubDir,
                out var resolvedMetadataDir))
            {
                backupSubDir = resolvedBackupSubDir;
                metadataDir = resolvedMetadataDir;
            }

            return new CloudCommandContext
            {
                ConfigName = string.IsNullOrWhiteSpace(config.Name) ? "DefaultConfig" : config.Name,
                ConfigId = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString() : config.Id,
                FolderName = folderName,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                BackupSubDir = backupSubDir,
                MetadataDir = metadataDir,
                ArchiveFileName = archiveFileName,
                ArchiveFilePath = Path.Combine(backupSubDir, archiveFileName),
                BackupMode = config.Archive?.Mode.ToString() ?? BackupMode.Full.ToString(),
                Comment = "ManualBackup",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            };
        }

        public static async Task<bool> UploadHistoryItemAsync(BackupConfig? config, ManagedFolder? folder, HistoryItem? item)
        {
            if (config == null || folder == null || item == null)
            {
                return false;
            }

            var settings = config.Cloud;
            if (settings?.Enabled != true)
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_NotEnabled"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (settings.CommandMode != CloudCommandMode.Rclone)
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
            if (settings?.Enabled != true)
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_NotEnabled"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (settings.CommandMode != CloudCommandMode.Rclone)
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_RcloneOnly"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (!item.IsCloudArchived || string.IsNullOrWhiteSpace(item.CloudArchiveRemotePath))
            {
                NotificationService.ShowWarning(I18n.GetString("CloudSync_Notification_NoCloudCopy"), I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            if (!TryBuildHistoryCloudPaths(config, folder, item, out var paths, out var errorMessage))
            {
                NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                return false;
            }

            var task = CreateTask(I18n.Format("CloudSync_Task_HistoryDownloadName", item.FileName), DownloadTaskIconGlyph);
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

                Directory.CreateDirectory(Path.GetDirectoryName(paths.ArchiveFilePath)!);
                if (!string.IsNullOrWhiteSpace(paths.MetadataDir))
                {
                    Directory.CreateDirectory(paths.MetadataDir);
                }

                var archiveCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.ArchiveRemotePath, paths.ArchiveFilePath));
                var archiveResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    archiveCommand,
                    I18n.GetString("CloudSync_Task_DownloadingArchive"),
                    item.FileName).ConfigureAwait(false);

                if (!archiveResult.Success)
                {
                    NotificationService.ShowWarning(
                        I18n.Format("CloudSync_Notification_HistoryDownloadFailed", item.FileName, archiveResult.ErrorMessage),
                        I18n.GetString("CloudSync_Notification_Title"));
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), archiveResult.ErrorMessage, archiveResult.ExitCode).ConfigureAwait(false);
                    return false;
                }

                await RunOnUIAsync(() => task.Progress = 40).ConfigureAwait(false);

                string? metadataWarning = null;
                if (!string.IsNullOrWhiteSpace(paths.MetadataStateRemotePath))
                {
                    var stateCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.MetadataStateRemotePath, paths.MetadataStateFilePath));
                    var stateResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        stateCommand,
                        I18n.GetString("CloudSync_Task_DownloadingMetadata"),
                        item.FileName).ConfigureAwait(false);

                    if (!stateResult.Success)
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

                if (!string.IsNullOrWhiteSpace(paths.MetadataRecordRemotePath))
                {
                    var recordCommand = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(paths.MetadataRecordRemotePath, paths.MetadataRecordFilePath));
                    var recordResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        recordCommand,
                        I18n.GetString("CloudSync_Task_DownloadingMetadata"),
                        item.FileName).ConfigureAwait(false);

                    if (!recordResult.Success)
                    {
                        metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", item.FileName);
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", item.FileName, recordResult.ErrorMessage), nameof(CloudSyncService));
                    }
                }
                else
                {
                    metadataWarning = I18n.Format("CloudSync_Notification_MetadataPartial", item.FileName);
                }

                NotificationService.ShowSuccess(
                    I18n.Format("CloudSync_Notification_HistoryDownloadSucceeded", item.FileName),
                    I18n.GetString("CloudSync_Notification_Title"));

                if (!string.IsNullOrWhiteSpace(metadataWarning))
                {
                    NotificationService.ShowWarning(metadataWarning, I18n.GetString("CloudSync_Notification_Title"));
                }

                await CompleteTaskAsync(task, settings, true, I18n.GetString("CloudSync_Task_DownloadCompleted"), string.Empty, 0).ConfigureAwait(false);
                return true;
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
                    MarkAutomaticUploadHistoryState(config, folder, settings, context);
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

        private static void MarkAutomaticUploadHistoryState(BackupConfig config, ManagedFolder folder, CloudSettings settings, CloudCommandContext context)
        {
            if (settings.CommandMode != CloudCommandMode.Rclone || settings.TemplateKind == CloudTemplateKind.Custom)
            {
                return;
            }

            var remotePaths = BuildDefaultRemotePaths(context.ConfigName, context.FolderName, context.ArchiveFileName, settings.RemoteBasePath);
            string metadataRecordRemotePath = settings.TemplateKind == CloudTemplateKind.UploadBackupDirectory ? remotePaths.MetadataRecordRemotePath : string.Empty;
            string metadataStateRemotePath = settings.TemplateKind == CloudTemplateKind.UploadBackupDirectory ? remotePaths.MetadataStateRemotePath : string.Empty;

            HistoryService.UpdateCloudArchiveState(
                config.Id,
                folder.Path,
                context.ArchiveFileName,
                true,
                DateTime.UtcNow,
                remotePaths.ArchiveRemotePath,
                metadataRecordRemotePath,
                metadataStateRemotePath);
        }

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
                var lines = source
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();

                if (lines.Length > 0)
                {
                    return lines[^1];
                }
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

        private static string ResolveRcloneExecutable(CloudSettings settings)
        {
            var executable = string.IsNullOrWhiteSpace(settings.ExecutablePath)
                ? DefaultRcloneExecutable
                : settings.ExecutablePath;
            return executable.Trim();
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

        private static bool TryBuildHistoryCloudPaths(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem item,
            out HistoryCloudPaths paths,
            out string errorMessage)
        {
            paths = null!;
            errorMessage = string.Empty;

            string archiveFilePath = HistoryService.GetBackupFilePath(config, folder, item) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(archiveFilePath))
            {
                errorMessage = I18n.GetString("History_ViewFile_PathEmpty");
                return false;
            }

            string folderName = string.IsNullOrWhiteSpace(item.FolderName)
                ? (folder.DisplayName ?? string.Empty)
                : item.FolderName;
            string destinationPath = config.DestinationPath ?? string.Empty;
            string metadataDir = Path.Combine(destinationPath, "_metadata", folderName);
            if (BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationPath,
                folderName,
                folder.Path,
                out _,
                out _,
                out var resolvedMetadataDir))
            {
                metadataDir = resolvedMetadataDir;
            }

            if (!BackupMetadataStoreService.TryGetStateFilePath(metadataDir, out var stateFilePath))
            {
                stateFilePath = Path.Combine(metadataDir, "state.json");
            }

            if (!BackupMetadataStoreService.TryGetRecordFilePath(metadataDir, item.FileName, out var recordFilePath))
            {
                recordFilePath = Path.Combine(metadataDir, "records", item.FileName + ".json");
            }

            var defaultRemotePaths = BuildDefaultRemotePaths(config.Name ?? string.Empty, folderName, item.FileName, config.Cloud?.RemoteBasePath);
            paths = new HistoryCloudPaths
            {
                FolderName = folderName,
                ArchiveFilePath = archiveFilePath,
                ArchiveRemotePath = string.IsNullOrWhiteSpace(item.CloudArchiveRemotePath) ? defaultRemotePaths.ArchiveRemotePath : item.CloudArchiveRemotePath,
                MetadataDir = metadataDir,
                MetadataStateFilePath = stateFilePath,
                MetadataRecordFilePath = recordFilePath,
                MetadataStateRemotePath = string.IsNullOrWhiteSpace(item.CloudMetadataStateRemotePath) ? defaultRemotePaths.MetadataStateRemotePath : item.CloudMetadataStateRemotePath,
                MetadataRecordRemotePath = string.IsNullOrWhiteSpace(item.CloudMetadataRecordRemotePath) ? defaultRemotePaths.MetadataRecordRemotePath : item.CloudMetadataRecordRemotePath
            };

            return true;
        }

        private static (string ArchiveRemotePath, string MetadataRecordRemotePath, string MetadataStateRemotePath) BuildDefaultRemotePaths(
            string configName,
            string folderName,
            string archiveFileName,
            string? remoteBasePath)
        {
            string normalizedRemoteBasePath = string.IsNullOrWhiteSpace(remoteBasePath)
                ? "remote:FolderRewind"
                : remoteBasePath.Trim().TrimEnd('/');

            string remoteFolderRoot = AppendRemotePath(normalizedRemoteBasePath, configName, folderName);
            return (
                AppendRemotePath(remoteFolderRoot, archiveFileName),
                AppendRemotePath(remoteFolderRoot, "_metadata", "records", archiveFileName + ".json"),
                AppendRemotePath(remoteFolderRoot, "_metadata", "state.json"));
        }

        private static string AppendRemotePath(string root, params string[] segments)
        {
            string result = root?.Trim().TrimEnd('/') ?? string.Empty;
            foreach (var rawSegment in segments)
            {
                var segment = (rawSegment ?? string.Empty).Trim().Trim('/');
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                result = string.IsNullOrWhiteSpace(result)
                    ? segment
                    : result + "/" + segment;
            }

            return result;
        }

        private static string BuildRcloneCopyToArguments(string sourcePath, string destinationPath)
        {
            return $"copyto {Quote(sourcePath)} {Quote(destinationPath)}";
        }

        private static (string Executable, string Arguments) WrapScriptExecutionIfNeeded(string executablePath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return (string.Empty, arguments);
            }

            string extension = Path.GetExtension(executablePath);
            if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                string combined = $"-ExecutionPolicy Bypass -File {Quote(executablePath)}";
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    combined += " " + arguments;
                }

                return (DefaultWindowsPowerShellExecutable, combined);
            }

            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                string combined = $"/c \"{executablePath}";
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    combined += " " + arguments;
                }

                combined += "\"";
                return (DefaultCommandShellExecutable, combined);
            }

            return (executablePath, arguments);
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "\"\"";
            }

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
            {
                return input ?? string.Empty;
            }

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
