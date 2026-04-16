using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class CloudSyncService
    {
        // 云同步与云存档的编排入口。
        // 这里主要负责命令组织、任务状态和错误反馈，具体历史/配置读写复用已有 Service。
        private const string DefaultRcloneExecutable = "rclone.exe";
        private const string DefaultWindowsPowerShellExecutable = "powershell.exe";
        private const string DefaultCommandShellExecutable = "cmd.exe";
        private const string UploadTaskIconGlyph = "\uE898";
        private const string DownloadTaskIconGlyph = "\uE896";
        private const int MaxRetryCount = 5;
        private const int MaxTimeoutSeconds = 86400;
        private const int MaxLogLength = 4096;
        // 故意串行执行云命令，避免并发 rclone 时任务状态、日志和元数据写入互相打架。
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
            return CanUseManualCloudActions(config);
        }

        public static bool CanUseManualCloudActions(BackupConfig? config)
        {
            return config?.Cloud != null
                && config.Cloud.CommandMode == CloudCommandMode.Rclone;
        }

        public static string GetEffectiveExecutablePath(BackupConfig? config)
        {
            return ResolveRcloneExecutable(config?.Cloud);
        }

        public static string GetSuggestedRemoteBasePath()
        {
            string globalDefault = ConfigService.CurrentConfig?.GlobalSettings?.DefaultCloudRemoteBasePath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(globalDefault))
            {
                return globalDefault;
            }

            var configured = ConfigService.CurrentConfig?.BackupConfigs?
                .Select(cfg => cfg?.Cloud?.RemoteBasePath?.Trim())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

            return string.IsNullOrWhiteSpace(configured)
                ? "remote:FolderRewind"
                : configured!;
        }

        public static async Task<(bool Success, int RecoveredCount, string Message)> DownloadConfigurationHistoryAsync(BackupConfig? config)
        {
            if (config == null)
            {
                return (false, 0, I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed"));
            }

            var settings = config.Cloud;
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

            string executablePath = ResolveRcloneExecutable(settings);
            string workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
            if (!ValidateExecutableAndWorkingDirectory(executablePath, workingDirectory, out var errorMessage))
            {
                NotificationService.ShowError(errorMessage, I18n.GetString("CloudSync_Notification_Title"));
                return (false, 0, errorMessage);
            }

            var task = CreateTask(I18n.Format("CloudSync_Task_ConfigurationDownloadName", config.Name), DownloadTaskIconGlyph);
            await RunOnUIAsync(() => BackupService.ActiveTasks.Insert(0, task)).ConfigureAwait(false);

            int recoveredCount = 0;
            int successFolderCount = 0;
            string lastFailure = string.Empty;

            await CommandSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("CloudSync_Task_Preparing");
                    task.IsIndeterminate = false;
                    task.Progress = 0;
                }).ConfigureAwait(false);

                // 逐目录拉取时先下载归档，再补拉元数据：即使元数据失败，至少也能恢复出可用备份包。
                for (int index = 0; index < config.SourceFolders.Count; index++)
                {
                    var folder = config.SourceFolders[index];
                    if (folder == null)
                    {
                        continue;
                    }

                    if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                        config.DestinationPath ?? string.Empty,
                        folder.DisplayName ?? string.Empty,
                        folder.Path,
                        out var storageFolderName,
                        out var backupSubDir,
                        out var metadataDir))
                    {
                        lastFailure = I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName ?? string.Empty, I18n.GetString("History_ViewFile_PathEmpty"));
                        LogService.LogWarning(lastFailure, nameof(CloudSyncService));
                        continue;
                    }

                    Directory.CreateDirectory(backupSubDir);
                    Directory.CreateDirectory(metadataDir);

                    string remoteFolderName = string.IsNullOrWhiteSpace(folder.DisplayName) ? storageFolderName : folder.DisplayName;
                    string remoteFolderRoot = AppendRemotePath(settings.RemoteBasePath ?? string.Empty, config.Name ?? string.Empty, remoteFolderName);

                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("CloudSync_Task_DownloadingFolderArchives", folder.DisplayName ?? string.Empty);
                        task.Progress = Math.Min(90, (index * 100.0) / Math.Max(config.SourceFolders.Count, 1));
                    }).ConfigureAwait(false);

                    List<string> remoteArchiveFiles = await ListRemoteFilesAsync(
                        executablePath,
                        workingDirectory,
                        settings,
                        remoteFolderRoot).ConfigureAwait(false);

                    var archiveCommand = CreateDirectCommand(
                        executablePath,
                        workingDirectory,
                        BuildRcloneCopyArguments(remoteFolderRoot, backupSubDir, "_metadata", "_metadata/**"));

                    var archiveResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        archiveCommand,
                        I18n.GetString("CloudSync_Task_DownloadingArchive"),
                        folder.DisplayName ?? string.Empty).ConfigureAwait(false);

                    if (!archiveResult.Success)
                    {
                        lastFailure = archiveResult.ErrorMessage;
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName ?? string.Empty, archiveResult.ErrorMessage), nameof(CloudSyncService));
                        continue;
                    }

                    string metadataRemoteRoot = AppendRemotePath(remoteFolderRoot, "_metadata");
                    var metadataCommand = CreateDirectCommand(
                        executablePath,
                        workingDirectory,
                        BuildRcloneCopyArguments(metadataRemoteRoot, metadataDir));

                    var metadataResult = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        metadataCommand,
                        I18n.GetString("CloudSync_Task_DownloadingMetadata"),
                        folder.DisplayName ?? string.Empty).ConfigureAwait(false);

                    if (!metadataResult.Success)
                    {
                        LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", folder.DisplayName ?? string.Empty, metadataResult.ErrorMessage), nameof(CloudSyncService));
                    }

                    recoveredCount += HistoryService.ScanAndRecoverHistory(backupSubDir, config, folder);

                    foreach (var archiveFile in remoteArchiveFiles)
                    {
                        var remotePaths = BuildDefaultRemotePaths(config.Name ?? string.Empty, remoteFolderName, archiveFile, settings.RemoteBasePath);
                        HistoryService.UpdateCloudArchiveState(
                            config.Id,
                            folder.Path,
                            archiveFile,
                            true,
                            DateTime.UtcNow,
                            remotePaths.ArchiveRemotePath,
                            remotePaths.MetadataRecordRemotePath,
                            remotePaths.MetadataStateRemotePath);
                    }

                    successFolderCount++;
                }

                bool success = successFolderCount > 0;
                string message = success
                    ? I18n.Format("CloudSync_Notification_ConfigurationDownloadSucceeded", config.Name ?? string.Empty, recoveredCount)
                    : string.IsNullOrWhiteSpace(lastFailure)
                        ? I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed")
                        : I18n.Format("CloudSync_Notification_ConfigurationDownloadFailedWithReason", config.Name ?? string.Empty, lastFailure);

                if (success)
                {
                    NotificationService.ShowSuccess(message, I18n.GetString("CloudSync_Notification_Title"));
                    await CompleteTaskAsync(task, settings, true, I18n.GetString("CloudSync_Task_DownloadCompleted"), string.Empty, 0).ConfigureAwait(false);
                }
                else
                {
                    NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
                    await CompleteTaskAsync(task, settings, false, I18n.GetString("CloudSync_Task_Failed"), message, -1).ConfigureAwait(false);
                }

                return (success, recoveredCount, message);
            }
            finally
            {
                CommandSemaphore.Release();
            }
        }

        public static async Task<ConfigCloudHistoryAnalysisResult> AnalyzeConfigurationHistoryAsync(BackupConfig? config)
        {
            return await AnalyzeConfigurationHistoryCoreAsync(config).ConfigureAwait(false);
        }

        public static async Task<ConfigCloudSyncResult> SyncConfigurationFromCloudAsync(BackupConfig? config, ConfigCloudSyncMode mode)
        {
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
                var downloadResult = await DownloadConfigurationHistoryAsync(config).ConfigureAwait(false);
                recoveredBackupCount = downloadResult.RecoveredCount;
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

        private static async Task<ConfigCloudHistoryAnalysisResult> AnalyzeConfigurationHistoryCoreAsync(BackupConfig? config)
        {
            if (config == null)
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed")
                };
            }

            if (!CanUseManualCloudActions(config))
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_RcloneOnly")
                };
            }

            if (config.SourceFolders == null || config.SourceFolders.Count == 0)
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_ConfigurationNoFolders")
                };
            }

            var remoteHistoryResult = await DownloadRemoteHistoryItemsAsync(config).ConfigureAwait(false);
            if (!remoteHistoryResult.Success)
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = remoteHistoryResult.Message
                };
            }

            var remoteItems = remoteHistoryResult.Items;
            string remoteConfigRoot = AppendRemotePath(config.Cloud?.RemoteBasePath ?? string.Empty, config.Name ?? string.Empty);
            // 优先按 ConfigId 精确匹配；旧历史可能没有 ConfigId，再退化到远端路径前缀匹配。
            var exactMatches = remoteItems
                .Where(item => string.Equals(item.ConfigId, config.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var matchedItems = exactMatches.Count > 0
                ? exactMatches
                : remoteItems.Where(item => HistoryItemMatchesRemoteConfigRoot(item, remoteConfigRoot)).ToList();

            var mappedItems = new List<HistoryItem>();
            int unmappedEntries = 0;
            int ambiguousEntries = 0;

            foreach (var remoteItem in matchedItems)
            {
                var mappedFolder = ResolveMappedFolder(config, remoteItem, out bool isAmbiguous);
                if (mappedFolder == null)
                {
                    if (isAmbiguous)
                    {
                        ambiguousEntries++;
                    }
                    else
                    {
                        unmappedEntries++;
                    }

                    continue;
                }

                mappedItems.Add(CloneMappedHistoryItem(config, mappedFolder, remoteItem));
            }

            string message = mappedItems.Count > 0
                ? I18n.Format("CloudSync_ConfigSync_AnalysisSummary", matchedItems.Count, mappedItems.Count, unmappedEntries, ambiguousEntries)
                : I18n.Format("CloudSync_ConfigSync_AnalysisEmpty", matchedItems.Count, unmappedEntries, ambiguousEntries);

            LogService.LogInfo(
                $"[CloudSyncService] Analyze config history: config={config.Name}, total={remoteItems.Count}, matched={matchedItems.Count}, mapped={mappedItems.Count}, unmapped={unmappedEntries}, ambiguous={ambiguousEntries}",
                nameof(CloudSyncService));

            return new ConfigCloudHistoryAnalysisResult
            {
                Success = true,
                Message = message,
                TotalRemoteEntries = remoteItems.Count,
                MatchedEntries = matchedItems.Count,
                ImportableEntries = mappedItems.Count,
                UnmappedEntries = unmappedEntries,
                AmbiguousEntries = ambiguousEntries,
                MappedItems = mappedItems
            };
        }

        private static async Task<(bool Success, string Message, List<HistoryItem> Items)> DownloadRemoteHistoryItemsAsync(BackupConfig config)
        {
            string remoteHistoryPath = AppendRemotePath(config.Cloud?.RemoteBasePath ?? string.Empty, "history.json");
            if (string.IsNullOrWhiteSpace(remoteHistoryPath))
            {
                return (false, I18n.GetString("CloudSync_Notification_HistoryImportFailed"), new List<HistoryItem>());
            }

            if (!TryResolveSharedRcloneRuntime(config.Cloud, out var executablePath, out var workingDirectory, out var errorMessage))
            {
                return (false, errorMessage, new List<HistoryItem>());
            }

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_history_analysis_{Guid.NewGuid():N}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath) ?? Path.GetTempPath());

            await CommandSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(remoteHistoryPath, tempFilePath));
                var result = await RunSilentCommandAsync(command, Math.Clamp(config.Cloud?.TimeoutSeconds ?? 600, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                if (!result.Success || !File.Exists(tempFilePath))
                {
                    string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? I18n.GetString("CloudSync_Notification_HistoryImportFailed")
                        : I18n.Format("CloudSync_Notification_ImportFailedWithReason", result.ErrorMessage);
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", config.Name ?? string.Empty, message), nameof(CloudSyncService));
                    return (false, message, new List<HistoryItem>());
                }

                string json = await File.ReadAllTextAsync(tempFilePath).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListHistoryItem) ?? new List<HistoryItem>();
                return (true, string.Empty, items);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[CloudSyncService] Failed to analyze remote history: {ex.Message}", nameof(CloudSyncService), ex);
                return (false, I18n.Format("CloudSync_Notification_ImportFailedWithReason", ex.Message), new List<HistoryItem>());
            }
            finally
            {
                CommandSemaphore.Release();
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static bool HistoryItemMatchesRemoteConfigRoot(HistoryItem item, string remoteConfigRoot)
        {
            if (item == null || string.IsNullOrWhiteSpace(remoteConfigRoot))
            {
                return false;
            }

            return HasRemotePrefix(item.CloudArchiveRemotePath, remoteConfigRoot)
                || HasRemotePrefix(item.CloudMetadataRecordRemotePath, remoteConfigRoot)
                || HasRemotePrefix(item.CloudMetadataStateRemotePath, remoteConfigRoot);
        }

        private static bool HasRemotePrefix(string? value, string remoteConfigRoot)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(remoteConfigRoot))
            {
                return false;
            }

            string normalizedValue = value.Trim();
            string normalizedPrefix = remoteConfigRoot.Trim().TrimEnd('/') + "/";
            return normalizedValue.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedValue.TrimEnd('/'), remoteConfigRoot.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static ManagedFolder? ResolveMappedFolder(BackupConfig config, HistoryItem remoteItem, out bool isAmbiguous)
        {
            isAmbiguous = false;

            if (!string.IsNullOrWhiteSpace(remoteItem.FolderPath))
            {
                var exactFolder = config.SourceFolders.FirstOrDefault(folder =>
                    string.Equals(folder.Path, remoteItem.FolderPath, StringComparison.OrdinalIgnoreCase));
                if (exactFolder != null)
                {
                    return exactFolder;
                }
            }

            if (string.IsNullOrWhiteSpace(remoteItem.FolderName))
            {
                return null;
            }

            // 显示名可能重复，只有唯一命中才自动映射，避免把历史导入到错误目录。
            var displayNameMatches = config.SourceFolders
                .Where(folder => string.Equals(folder.DisplayName?.Trim(), remoteItem.FolderName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (displayNameMatches.Count == 1)
            {
                return displayNameMatches[0];
            }

            if (displayNameMatches.Count > 1)
            {
                isAmbiguous = true;
            }

            return null;
        }

        private static HistoryItem CloneMappedHistoryItem(BackupConfig config, ManagedFolder folder, HistoryItem remoteItem)
        {
            bool hasCloudCopy = remoteItem.IsCloudArchived || !string.IsNullOrWhiteSpace(remoteItem.CloudArchiveRemotePath);
            return new HistoryItem
            {
                ConfigId = config.Id,
                FolderPath = folder.Path ?? string.Empty,
                FolderName = folder.DisplayName ?? remoteItem.FolderName ?? string.Empty,
                FileName = remoteItem.FileName ?? string.Empty,
                Timestamp = remoteItem.Timestamp,
                BackupType = remoteItem.BackupType ?? string.Empty,
                Comment = remoteItem.Comment ?? string.Empty,
                IsImportant = remoteItem.IsImportant,
                IsCloudArchived = hasCloudCopy,
                CloudArchivedAtUtc = remoteItem.CloudArchivedAtUtc,
                CloudArchiveRemotePath = remoteItem.CloudArchiveRemotePath ?? string.Empty,
                CloudMetadataRecordRemotePath = remoteItem.CloudMetadataRecordRemotePath ?? string.Empty,
                CloudMetadataStateRemotePath = remoteItem.CloudMetadataStateRemotePath ?? string.Empty
            };
        }

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
                        var historyUploadWarning = await UploadHistorySnapshotAfterBackupAsync(task, settings).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(historyUploadWarning))
                        {
                            NotificationService.ShowWarning(historyUploadWarning, I18n.GetString("CloudSync_Notification_Title"));
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

            return result.Output
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
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

        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
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
            // 历史项若已有远端路径则优先复用，避免远端目录结构调整后被“默认路径”覆盖。
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
            // 远端路径统一使用 '/'，不要混入本地路径分隔符。
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

        private static string BuildRcloneCopyArguments(string sourcePath, string destinationPath, params string[] excludePatterns)
        {
            var builder = new StringBuilder();
            builder.Append("copy ");
            builder.Append(Quote(sourcePath));
            builder.Append(' ');
            builder.Append(Quote(destinationPath));

            foreach (var pattern in excludePatterns ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                builder.Append(" --exclude ");
                builder.Append(Quote(pattern.Trim()));
            }

            return builder.ToString();
        }

        private static string BuildRcloneListFileArguments(string remotePath)
        {
            return $"lsf {Quote(remotePath)} --files-only --max-depth 1";
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
