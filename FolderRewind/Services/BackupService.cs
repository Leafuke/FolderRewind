using FolderRewind.Models;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Abstractions;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        public static ObservableCollection<BackupTask> ActiveTasks { get; } = new();

        // 还原阶段会用内部标记目录记录“仅删除”动作，完成后必须清理避免污染用户目录。
        private const string InternalRestoreMarkerDirectoryName = "__FolderRewind_Internal";
        private const string InternalRestoreMarkerFileName = "__DeletedOnly.marker";
        private const string MissingEncryptionPasswordMessage = "Encrypted backup password is missing for this configuration.";

        // 变更集是增量备份/删除标记/元数据写入的统一输入。
        private sealed class BackupChangeSet
        {
            public List<string> AddedFiles { get; } = new();
            public List<string> ModifiedFiles { get; } = new();
            public List<string> DeletedFiles { get; } = new();

            public bool HasChanges => AddedFiles.Count > 0
                || ModifiedFiles.Count > 0
                || DeletedFiles.Count > 0;
        }

        private sealed class SmartRestoreArchiveGroup
        {
            public required FileInfo Archive { get; init; }
            public required List<string> Files { get; init; }
        }

        private sealed class SmartRestorePlan
        {
            public required List<FileInfo> Chain { get; init; }
            public required List<SmartRestoreArchiveGroup> ArchiveGroups { get; init; }
        }

        private enum RestoreChainBuildStatus
        {
            Success = 0,
            MissingBaseFull = 1,
            NotFound = 2
        }

        public sealed class DeleteBackupResult
        {
            public bool Success { get; init; }
            public bool ArchiveDeleted { get; init; }
            public bool HistoryUpdated { get; init; }
            public string Message { get; init; } = string.Empty;
        }

        private sealed class DeleteArchiveExecutionResult
        {
            public bool Success { get; set; }
            public bool ArchiveDeleted { get; set; }
            public bool HistoryUpdated { get; set; }
            public string DeletedFileName { get; set; } = string.Empty;
            public string? RenamedFromFileName { get; set; }
            public string? RenamedToFileName { get; set; }
            public string? RenamedToBackupType { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        // 压缩模式除了成功/失败，还必须区分“精确来源当前没有匹配文件”，避免把可预期的缺席误报为错误。
        private readonly record struct BackupArchiveExecutionResult(
            bool Success,
            string? FileName,
            bool IsUnavailable)
        {
            public static BackupArchiveExecutionResult Created(string fileName) => new(true, fileName, false);
            public static BackupArchiveExecutionResult NoChanges => new(true, null, false);
            public static BackupArchiveExecutionResult Unavailable => new(true, null, true);
            public static BackupArchiveExecutionResult Failed => new(false, null, false);
        }

        private sealed class BackupSourceExecutionOutcome
        {
            public BackupRunSourceStatus Status { get; init; }
            public Guid? FolderId { get; init; }
            public string FolderPath { get; init; } = string.Empty;
            public string FolderName { get; init; } = string.Empty;
            public HistoryItem? HistoryItem { get; init; }
            public string ErrorMessage { get; init; } = string.Empty;
            public bool CreatedNewArchive => Status == BackupRunSourceStatus.NewArchive;

            public BackupRunSourceRecord ToRunSource() => new()
            {
                FolderId = FolderId,
                FolderPath = FolderPath,
                FolderName = FolderName,
                Status = Status,
                HistoryItemId = HistoryItem?.Id ?? string.Empty,
                ArchiveFileName = HistoryItem?.FileName ?? string.Empty,
                ErrorMessage = ErrorMessage
            };
        }

        private static void BroadcastBackupEvent(
            int configIndex,
            BackupConfig config,
            ManagedFolder folder,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            var context = KnotLinkService.CurrentCommandContext;
            var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName
            };
            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            KnotLinkService.BroadcastEvent(context, eventName, merged);
        }

        private static void BroadcastBackupLifecycle(string lifecycleEvent, IReadOnlyDictionary<string, string?>? fields = null)
        {
            var context = KnotLinkService.CurrentCommandContext;
            if (context?.Metadata.HasConversation == true
                && string.Equals(context.Command, "BACKUP", StringComparison.OrdinalIgnoreCase))
            {
                KnotLinkService.BroadcastCommandLifecycle(context, lifecycleEvent, fields);
            }
        }

        private static void BroadcastRestoreEvent(
            int configIndex,
            BackupConfig config,
            ManagedFolder folder,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            var context = KnotLinkService.CurrentCommandContext;
            var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName
            };
            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            KnotLinkService.BroadcastEvent(context, eventName, merged);
        }

        private static void BroadcastRestoreLifecycle(string lifecycleEvent, IReadOnlyDictionary<string, string?>? fields = null)
        {
            var context = KnotLinkService.CurrentCommandContext;
            if (context?.Metadata.HasConversation == true
                && string.Equals(context.Command, "RESTORE", StringComparison.OrdinalIgnoreCase))
            {
                KnotLinkService.BroadcastCommandLifecycle(context, lifecycleEvent, fields);
            }
        }


        // 主备份编排保留在入口文件中；压缩、过滤、元数据和还原细节拆到同名 partial 文件。



        /// <summary>
        /// 备份配置下的所有文件夹
        /// </summary>
        /// <returns>true 表示至少有一个文件夹产生了新的备份文件；false 表示所有文件夹均未检测到变更。</returns>
        public static async Task<bool> BackupConfigAsync(
            BackupConfig config,
            BackupInvocationOptions? invocationOptions = null)
        {
            if (config == null) return false;
            invocationOptions ??= BackupInvocationOptions.Default;
            Log(I18n.Format("BackupService_Log_ConfigTaskBegin", config.Name), LogLevel.Info);

            bool anyChanges = false;
            var startedAtUtc = DateTime.UtcNow;
            var runId = Guid.NewGuid().ToString("N");
            var sourceOutcomes = new List<BackupSourceExecutionOutcome>();
            foreach (var folder in config.SourceFolders)
            {
                var outcome = await BackupFolderCoreAsync(
                    config,
                    folder,
                    comment: invocationOptions.Comment,
                    invocationOptions: invocationOptions,
                    createdByRunId: runId);
                sourceOutcomes.Add(outcome);
                if (outcome.CreatedNewArchive) anyChanges = true;
            }

            var run = BackupRunPolicy.Create(
                runId,
                config.Id,
                startedAtUtc,
                DateTime.UtcNow,
                MapRunTriggerSource(invocationOptions.Source),
                invocationOptions.Comment,
                sourceOutcomes.Select(outcome => outcome.ToRunSource()));
            if (run != null)
            {
                if (BackupRunService.Add(run))
                {
                    BackupRunService.ApplyRetention(config);
                    CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(
                        config,
                        "configuration backup run completion");
                }
            }
            await PruneRetainedSourceArchivesAsync(config);

            Log(I18n.Format("BackupService_Log_TaskEnd"), LogLevel.Info);
            return anyChanges;
        }

        /// <summary>
        /// 备份单个文件夹
        /// </summary>
        /// <returns>true 表示产生了新的备份文件；false 表示未检测到变更或备份失败。</returns>
        public static async Task<bool> BackupFolderAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions? invocationOptions = null)
        {
            invocationOptions ??= BackupInvocationOptions.Default;
            var outcome = await BackupFolderCoreAsync(
                config,
                folder,
                invocationOptions.Comment,
                invocationOptions,
                createdByRunId: null);
            if (outcome.CreatedNewArchive)
            {
                await PruneRetainedSourceArchivesAsync(config);
            }
            return outcome.CreatedNewArchive;
        }

        private static async Task<BackupSourceExecutionOutcome> BackupFolderCoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            string? comment,
            BackupInvocationOptions? invocationOptions,
            string? createdByRunId)
        {
            if (config == null || folder == null)
            {
                return new BackupSourceExecutionOutcome
                {
                    Status = BackupRunSourceStatus.Failed,
                    ErrorMessage = "Invalid backup configuration or source."
                };
            }
            comment ??= string.Empty;
            invocationOptions ??= BackupInvocationOptions.Default;

            int configIndex = GetConfigIndex(config);

            // 1. 创建任务对象并确保在 UI 线程添加到集合
            var task = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = I18n.Format("BackupService_Task_Preparing"),
                Progress = 0
            };

            await RunOnUIAsync(() => ActiveTasks.Insert(0, task));

            await using var v3Session = await PluginV3BackupSession.PrepareAsync(config, folder);
            if (v3Session.IsBlocked)
            {
                var diagnostic = v3Session.Diagnostics.LastOrDefault();
                var message = diagnostic is null
                    ? "The v3 plugin policy blocked this backup."
                    : $"{diagnostic.Code} ({diagnostic.Owner})";
                Log($"[PluginV3] {message}", LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = message;
                });
                return CreateSourceOutcome(folder, BackupRunSourceStatus.Failed, errorMessage: message);
            }

            var runtimeConfig = v3Session.EffectiveConfig;
            var runtimeFolder = v3Session.EffectiveFolder;

            var scopeResolution = Services.Plugins.PluginService.ResolveConfigWithBackupFilterContributions(runtimeConfig, runtimeFolder);
            if (!scopeResolution.Success)
            {
                string scopeError = string.IsNullOrWhiteSpace(scopeResolution.ErrorMessage)
                    ? I18n.Format("PluginService_BackupScope_Invalid")
                    : scopeResolution.ErrorMessage;
                Log($"[PluginScope] {scopeResolution.ErrorCode}: {scopeError}", LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Task_Failed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = scopeError;
                });
                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = scopeResolution.ErrorCode,
                    ["error"] = scopeError
                });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = scopeResolution.ErrorCode,
                    ["message"] = scopeError
                });
                return CreateSourceOutcome(folder, BackupRunSourceStatus.Failed, errorMessage: scopeError);
            }

            runtimeConfig = scopeResolution.EffectiveConfig;
            if (!TryValidateBackupFilterRules(runtimeConfig.Filters, out string filterValidationError))
            {
                Log($"[Filter] Backup filter validation failed: {filterValidationError}", LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Task_Failed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = filterValidationError;
                });
                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = "invalid_filter_rule",
                    ["error"] = filterValidationError
                });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = "invalid_filter_rule",
                    ["message"] = filterValidationError
                });
                return CreateSourceOutcome(folder, BackupRunSourceStatus.Failed, errorMessage: filterValidationError);
            }

            if (string.IsNullOrEmpty(runtimeConfig.DestinationPath))
            {
                Log(I18n.Format("BackupService_Log_DestinationNotSet"), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_TargetNotSet");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = I18n.Format("BackupService_Folder_TargetNotSet");
                });

                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "command_failed" });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?> { ["error"] = "command_failed" });
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Failed,
                    errorMessage: I18n.Format("BackupService_Folder_TargetNotSet"));
            }

            if (!TryResolveBackupStoragePaths(
                runtimeConfig.DestinationPath,
                folder.DisplayName,
                folder.Path,
                out var storageFolderName,
                out var backupSubDir,
                out var metadataDir))
            {
                string invalidFolderNameMessage = I18n.GetString("BackupService_Log_InvalidStorageFolderName");
                Log(invalidFolderNameMessage, LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Task_Failed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = invalidFolderNameMessage;
                });

                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "invalid_folder_name" });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?> { ["error"] = "invalid_folder_name" });
                return CreateSourceOutcome(folder, BackupRunSourceStatus.Failed, errorMessage: invalidFolderNameMessage);
            }

            async Task<BackupSourceExecutionOutcome?> RejectOverlappingPathAsync(string candidateSourcePath)
            {
                var overlap = BackupPathOverlapPolicy.Validate(candidateSourcePath, backupSubDir, metadataDir);
                if (overlap.IsSafe)
                {
                    return null;
                }

                var overlapMessage = I18n.Format(
                    "BackupService_Folder_SourceDestinationOverlap",
                    overlap.SourcePath,
                    overlap.TargetPath);
                Log(I18n.Format("BackupService_Log_SourceDestinationOverlap", overlap.SourcePath, overlap.TargetPath), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Task_Failed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = overlapMessage;
                });
                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = "source_destination_overlap",
                    ["error"] = overlapMessage
                });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = "source_destination_overlap",
                    ["message"] = overlapMessage
                });
                return CreateSourceOutcome(folder, BackupRunSourceStatus.Failed, errorMessage: overlapMessage);
            }

            var configuredPathFailure = await RejectOverlappingPathAsync(folder.Path);
            if (configuredPathFailure != null)
            {
                return configuredPathFailure;
            }

            try
            {
                await v3Session.AcquireConsistencyAsync();
            }
            catch (Exception ex)
            {
                Log($"[PluginV3] Consistency acquisition failed: {ex.Message}", LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });
                return CreateSourceOutcome(folder, BackupRunSourceStatus.Failed, errorMessage: ex.Message);
            }

            // 插件接管同样只能收到已经解析并验证过的运行配置，不能绕过范围的失败关闭策略。
            var activeOwner = new PluginId(config.Kind?.OwnerId ?? "folderrewind.core");
            var (shouldHandle, handlerPlugin) = PluginV3RuntimeService.IsActive(activeOwner)
                ? (false, null)
                : Services.Plugins.PluginService.CheckPluginWantsToHandleBackup(runtimeConfig);
            if (shouldHandle && handlerPlugin != null)
            {
                return await HandlePluginBackupAsync(runtimeConfig, folder, task, handlerPlugin, comment, createdByRunId);
            }

            // 允许插件在备份前创建快照并替换源路径（例如 Minecraft 热备份：先复制到 snapshot 再备份）。
            string sourcePath = v3Session.SourcePath;
            try
            {
                var ownerId = new PluginId(config.Kind?.OwnerId ?? "folderrewind.core");
                var pluginOverride = PluginV3RuntimeService.IsActive(ownerId)
                    ? null
                    : Services.Plugins.PluginService.InvokeBeforeBackupFolder(runtimeConfig, runtimeFolder, invocationOptions);
                if (!string.IsNullOrWhiteSpace(pluginOverride))
                {
                    sourcePath = pluginOverride;
                }
            }
            catch
            {
                // 插件异常不会影响核心备份流程（具体异常会在 PluginService 内记录）
            }
            if (!string.Equals(sourcePath, folder.Path, StringComparison.OrdinalIgnoreCase))
            {
                var overridePathFailure = await RejectOverlappingPathAsync(sourcePath);
                if (overridePathFailure != null)
                {
                    return overridePathFailure;
                }
            }
            if (!Directory.Exists(sourcePath))
            {
                Log(I18n.Format("BackupService_Log_SourceFolderMissing", sourcePath), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_SourceNotFound");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = I18n.Format("BackupService_Folder_SourceNotFound");
                });

                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "command_failed" });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?> { ["error"] = "command_failed" });
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Unavailable,
                    errorMessage: I18n.Format("BackupService_Folder_SourceNotFound"));
            }

            // 创建必要的目录
            if (!Directory.Exists(backupSubDir)) Directory.CreateDirectory(backupSubDir);
            if (!Directory.Exists(metadataDir)) Directory.CreateDirectory(metadataDir);

            Log(I18n.Format("BackupService_Log_ProcessingFolder", folder.DisplayName), LogLevel.Info);
            await RunOnUIAsync(() => folder.StatusText = I18n.Format("BackupService_Folder_BackupInProgress"));

            // 与 MineBackup 保持一致：备份开始事件
            BroadcastBackupLifecycle("command_started");
            BroadcastBackupLifecycle("command_progress", new Dictionary<string, string?> { ["progress"] = "0" });
            BroadcastBackupEvent(configIndex, config, folder, "backup_started");

            bool success = false;
            bool sourceUnavailable = false;
            string? generatedFileName = null;
            HistoryItem? generatedHistoryItem = null;
            try
            {

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Processing");
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupRunning");
                });

                // 调用核心逻辑，传入 task 以便更新进度

                // 根据模式分发逻辑
                switch (config.Archive.Mode)
                {
                    case BackupMode.Incremental:
                        {
                            var res = await DoSmartBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Overwrite:
                        {
                            var res = await DoOverwriteBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Full:
                    default:
                        {
                            var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_Exception", ex.Message), LogLevel.Error);
                success = false;
                await RunOnUIAsync(() => { if (string.IsNullOrEmpty(task.ErrorMessage)) task.ErrorMessage = ex.Message; });
            }

            // The consistency lease spans source validation, diff calculation and
            // archive creation, but cleanup finishes before History/Cloud commit.
            await v3Session.CompleteCaptureAsync();

            PluginV3ArtifactCommitResult? artifactCommit = null;
            string? pendingHistoryItemId = null;
            if (success && !sourceUnavailable && !string.IsNullOrWhiteSpace(generatedFileName))
            {
                try
                {
                    pendingHistoryItemId = Guid.NewGuid().ToString("N");
                    artifactCommit = await PluginV3ArtifactService.CommitBackupAsync(
                        config,
                        folder,
                        pendingHistoryItemId,
                        Path.Combine(backupSubDir, generatedFileName),
                        generatedFileName,
                        IsPartialBackupFilter(runtimeConfig.Filters) || runtimeFolder.SourceScope.IsPartial,
                        v3Session.Diagnostics);
                }
                catch (Exception ex)
                {
                    success = false;
                    Log($"[PluginV3] Artifact commit failed: {ex.Message}", LogLevel.Error);
                    await RunOnUIAsync(() => task.ErrorMessage = ex.Message);
                }
            }

            if (sourceUnavailable)
            {
                string unavailableMessage = I18n.GetString("BackupService_Folder_NoMatchingFiles");
                await RunOnUIAsync(() =>
                {
                    task.Status = unavailableMessage;
                    task.Progress = 100;
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = true;
                    task.ErrorMessage = string.Empty;
                    folder.StatusText = unavailableMessage;
                });

                Log(I18n.Format("BackupService_Log_NoMatchingFiles", folder.DisplayName), LogLevel.Warning);
                BroadcastBackupEvent(configIndex, config, folder, "backup_unavailable", new Dictionary<string, string?>
                {
                    ["reason"] = "no_matching_files"
                });
                BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
                {
                    ["result"] = "unavailable",
                    ["reason"] = "no_matching_files"
                });
            }
            else if (success)
            {
                var completedFileName = string.IsNullOrWhiteSpace(generatedFileName) ? null : generatedFileName;
                bool hasNewFile = completedFileName != null;

                if (completedFileName != null)
                {
                    ConfigService.Save();

                    // 增量模式下，根据实际生成的文件名区分 Full 和 Smart
                    string typeStr;
                    if (runtimeConfig.Archive.Mode == BackupMode.Incremental)
                    {
                        typeStr = completedFileName.StartsWith("[Full]", StringComparison.OrdinalIgnoreCase) ? "Full" : "Smart";
                    }
                    else
                    {
                        typeStr = runtimeConfig.Archive.Mode.ToString();
                    }
                    generatedHistoryItem = HistoryService.AddEntry(
                        config,
                        folder,
                        completedFileName,
                        typeStr,
                        comment,
                        storageFolderName,
                        IsPartialBackupFilter(runtimeConfig.Filters) || runtimeFolder.SourceScope.IsPartial,
                        createdByRunId,
                        pendingHistoryItemId,
                        artifactCommit?.RootArtifactId.Value,
                        artifactCommit?.GraphRevision.Value,
                        artifactCommit is null
                            ? PersistedOperationOutcome.Success
                            : PluginV3ModelMapper.ToPersisted(artifactCommit.Outcome),
                        artifactCommit?.Diagnostics.Select(PluginV3ModelMapper.ToRecord).ToArray());

                    // 备份完成后检查文件大小，过小时发出警告
                    try
                    {
                        var archiveFile = Path.Combine(backupSubDir, completedFileName);
                        if (File.Exists(archiveFile))
                        {
                            var fileSizeKB = new FileInfo(archiveFile).Length / 1024.0;
                            var thresholdKB = ConfigService.CurrentConfig?.GlobalSettings?.FileSizeWarningThresholdKB ?? 5;
                            if (thresholdKB > 0 && fileSizeKB < thresholdKB)
                            {
                                Log(I18n.Format("BackupService_Log_FileSizeTooSmall", folder.DisplayName, fileSizeKB.ToString("F1"), thresholdKB.ToString()), LogLevel.Warning);
                                NotificationService.ShowWarning(
                                    I18n.Format("BackupService_Warning_FileSizeTooSmall", folder.DisplayName, fileSizeKB.ToString("F1"), thresholdKB.ToString()));
                                BroadcastBackupEvent(configIndex, config, folder, "backup_warning", new Dictionary<string, string?>
                                {
                                    ["type"] = "file_too_small",
                                    ["size_kb"] = fileSizeKB.ToString("F1")
                                });
                            }
                        }
                    }
                    catch
                    {
                    }

                    // 与 MineBackup 保持一致：备份成功事件
                    BroadcastBackupEvent(configIndex, config, folder, "backup_success", new Dictionary<string, string?>
                    {
                        ["file"] = completedFileName
                    });
                    BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
                    {
                        ["result"] = "created",
                        ["file"] = completedFileName
                    });

                    CloudSyncService.QueueUploadAfterBackup(config, folder, completedFileName, comment);
                    if (artifactCommit is not null && generatedHistoryItem is not null)
                    {
                        await PluginV3ArtifactService.ObserveCompletionAsync(
                            createdByRunId ?? generatedHistoryItem.Id,
                            config,
                            folder,
                            generatedHistoryItem,
                            artifactCommit);
                    }
                }

                await RunOnUIAsync(() =>
                {
                    task.Status = hasNewFile
                        ? I18n.Format("BackupService_Task_Completed")
                        : I18n.Format("BackupService_Task_NoChanges");
                    task.Progress = 100;
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = true;

                    folder.StatusText = hasNewFile
                        ? I18n.Format("BackupService_Folder_BackupCompleted")
                        : I18n.Format("BackupService_Task_NoChanges");
                    if (hasNewFile)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });

                if (!hasNewFile)
                {
                    BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
                    {
                        ["result"] = "no_changes"
                    });
                }

                Log(
                    hasNewFile
                        ? I18n.Format("BackupService_Log_BackupSucceeded", folder.DisplayName)
                        : I18n.Format("BackupService_Log_BackupSkippedNoChanges", folder.DisplayName),
                    LogLevel.Info);
            }
            else
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });
                Log(I18n.Format("BackupService_Log_BackupFailed", folder.DisplayName), LogLevel.Error);

                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "command_failed" });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?> { ["error"] = "command_failed" });

                // 发送失败通知
                NotificationService.NotifyBackupCompleted(folder.DisplayName, false, I18n.GetString("BackupService_Task_Failed"));
            }

            // 备份后回调（用于清理快照等）
            try
            {
                var ownerId = new PluginId(config.Kind?.OwnerId ?? "folderrewind.core");
                if (!PluginV3RuntimeService.IsActive(ownerId))
                {
                    Services.Plugins.PluginService.InvokeAfterBackupFolder(config, folder, success, generatedFileName);
                }
            }
            catch
            {
            }

            if (sourceUnavailable)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Unavailable,
                    errorMessage: I18n.GetString("BackupService_Folder_NoMatchingFiles"));
            }
            if (!success)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Failed,
                    errorMessage: task.ErrorMessage);
            }
            if (!string.IsNullOrWhiteSpace(generatedFileName))
            {
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.NewArchive,
                    generatedHistoryItem);
            }

            var reusedHistory = HistoryService.GetLatestEntryForFolder(config.Id, folder);
            return reusedHistory == null
                ? CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Unavailable,
                    errorMessage: "No changes were detected, but no previous successful history item exists.")
                : CreateSourceOutcome(folder, BackupRunSourceStatus.Reused, reusedHistory);
        }

        private static BackupSourceExecutionOutcome CreateSourceOutcome(
            ManagedFolder folder,
            BackupRunSourceStatus status,
            HistoryItem? historyItem = null,
            string? errorMessage = null) => new()
        {
            Status = status,
            FolderId = Guid.TryParse(folder.Id, out var folderId) ? folderId : null,
            FolderPath = folder.Path ?? string.Empty,
            FolderName = folder.DisplayName ?? string.Empty,
            HistoryItem = historyItem,
            ErrorMessage = errorMessage ?? string.Empty
        };

        private static BackupRunTriggerSource MapRunTriggerSource(BackupInvocationSource source) => source switch
        {
            BackupInvocationSource.Manual => BackupRunTriggerSource.Manual,
            BackupInvocationSource.Automatic => BackupRunTriggerSource.Automatic,
            BackupInvocationSource.Remote => BackupRunTriggerSource.Remote,
            BackupInvocationSource.PluginHotkey => BackupRunTriggerSource.PluginHotkey,
            BackupInvocationSource.Internal => BackupRunTriggerSource.Internal,
            _ => BackupRunTriggerSource.Unknown
        };

        private static async Task PruneRetainedSourceArchivesAsync(BackupConfig config)
        {
            if (config.Archive.KeepCount <= 0)
            {
                return;
            }

            var historyItems = HistoryService.GetEntriesForConfig(config.Id);
            var removableIds = BackupRunPolicy.SelectHistoryItemIdsToRemove(
                historyItems.Select(item => new BackupRetentionHistoryRecord
                {
                    HistoryItemId = item.Id,
                    SourcePath = item.FolderPath,
                    Timestamp = item.Timestamp,
                    IsImportant = item.IsImportant
                }),
                BackupRunService.GetRuns(config.Id),
                config.Archive.KeepCount)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var historyItem in historyItems.Where(item => removableIds.Contains(item.Id)))
            {
                var folder = config.SourceFolders.FirstOrDefault(candidate =>
                    string.Equals(candidate.Path, historyItem.FolderPath, StringComparison.OrdinalIgnoreCase))
                    ?? new ManagedFolder
                    {
                        Path = historyItem.FolderPath,
                        DisplayName = historyItem.FolderName
                    };
                var deletion = await DeleteBackupAsync(
                    config,
                    folder,
                    historyItem,
                    BackupDeleteMode.LocalArchiveAndRecord);
                if (!deletion.Success)
                {
                    Log(
                        $"[Retention] Failed to prune archive '{historyItem.FileName}': {deletion.Message}",
                        LogLevel.Warning);
                }
            }
        }

        public static async Task<bool> DeleteBackupRunAsync(BackupConfig config, BackupRunRecord run)
        {
            if (config == null || run == null
                || !string.Equals(config.Id, run.ConfigId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var removed = BackupRunService.Remove(run.RunId);
            if (removed == null)
            {
                return false;
            }
            await PruneRetainedSourceArchivesAsync(config);
            CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(config, "configuration backup run deletion");
            return true;
        }

        /// <summary>
        /// 由插件完全接管的备份流程
        /// </summary>
        private static async Task<BackupSourceExecutionOutcome> HandlePluginBackupAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupTask task,
            Services.Plugins.IFolderRewindPlugin plugin,
            string comment,
            string? createdByRunId)
        {
            Log(I18n.Format("BackupService_Log_PluginTakeover", plugin.Manifest.Name, folder.DisplayName), LogLevel.Info);
            var configIndex = GetConfigIndex(config);

            BroadcastBackupLifecycle("command_started");
            BroadcastBackupLifecycle("command_progress", new Dictionary<string, string?> { ["progress"] = "0" });
            if (KnotLinkService.CurrentCommandContext?.Metadata.HasConversation == true)
            {
                BroadcastBackupEvent(configIndex, config, folder, "backup_started");
            }

            await RunOnUIAsync(() =>
            {
                task.Status = I18n.Format("BackupService_Task_PluginProcessing");
                folder.StatusText = I18n.Format("BackupService_Folder_PluginBackingUp");
            });

            try
            {
                var result = await Services.Plugins.PluginService.InvokePluginBackupAsync(
                    plugin, config, folder, comment,
                    async (progress, status) =>
                    {
                        await RunOnUIAsync(() =>
                        {
                            task.Progress = progress;
                            task.Status = status;
                        });
                    });

                if (result.Success)
                {
                    bool hasNewFile = !string.IsNullOrWhiteSpace(result.GeneratedFileName);
                    HistoryItem? newHistoryItem = null;

                    await RunOnUIAsync(() =>
                    {
                        task.Status = hasNewFile
                            ? I18n.Format("BackupService_Task_Completed")
                            : I18n.Format("BackupService_Task_NoChanges");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;

                        folder.StatusText = hasNewFile
                            ? I18n.Format("BackupService_Folder_BackupCompleted")
                            : I18n.Format("BackupService_Task_NoChanges");
                        if (hasNewFile)
                        {
                            folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                        }
                    });

                    if (hasNewFile)
                    {
                        ConfigService.Save();
                        if (!TryResolveStorageFolderName(folder.DisplayName, folder.Path, out var storageFolderName))
                        {
                            throw new InvalidOperationException(I18n.GetString("BackupService_Log_InvalidStorageFolderName"));
                        }

                        newHistoryItem = HistoryService.AddEntry(
                            config,
                            folder,
                            result.GeneratedFileName!,
                            "Plugin",
                            comment,
                            storageFolderName,
                            IsPartialBackupFilter(config.Filters) || folder.SourceScope.IsPartial,
                            createdByRunId);
                        CloudSyncService.QueueUploadAfterBackup(config, folder, result.GeneratedFileName, comment);
                    }

                    Log(
                        hasNewFile
                            ? I18n.Format("BackupService_Log_PluginBackupSucceeded", folder.DisplayName)
                            : I18n.Format("BackupService_Log_PluginBackupSkippedNoChanges", folder.DisplayName),
                        LogLevel.Info);

                    if (hasNewFile)
                    {
                        BroadcastBackupEvent(configIndex, config, folder, "backup_success", new Dictionary<string, string?>
                        {
                            ["file"] = result.GeneratedFileName
                        });
                        BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
                        {
                            ["result"] = "created",
                            ["file"] = result.GeneratedFileName
                        });
                    }
                    else
                    {
                        BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
                        {
                            ["result"] = "no_changes"
                        });
                    }

                    if (hasNewFile)
                    {
                        return CreateSourceOutcome(
                            folder,
                            BackupRunSourceStatus.NewArchive,
                            newHistoryItem);
                    }

                    var reusedHistory = HistoryService.GetLatestEntryForFolder(config.Id, folder.Path);
                    return reusedHistory == null
                        ? CreateSourceOutcome(
                            folder,
                            BackupRunSourceStatus.Unavailable,
                            errorMessage: "The plugin reported no changes, but no previous successful history item exists.")
                        : CreateSourceOutcome(folder, BackupRunSourceStatus.Reused, reusedHistory);
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_Failed");
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = false;
                        task.ErrorMessage = result.Message ?? string.Empty;
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    });

                    Log(I18n.Format("BackupService_Log_PluginBackupFailed", folder.DisplayName, result.Message ?? string.Empty), LogLevel.Error);
                    BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "plugin_failed" });
                    BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                    {
                        ["error"] = result.Message ?? "plugin_failed"
                    });
                    return CreateSourceOutcome(
                        folder,
                        BackupRunSourceStatus.Failed,
                        errorMessage: result.Message ?? "plugin_failed");
                }
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Exception");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                    folder.StatusText = I18n.Format("BackupService_Folder_PluginException");
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);
                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = "plugin_exception",
                    ["error"] = ex.Message
                });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = ex.Message
                });
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Failed,
                    errorMessage: ex.Message);
            }
        }

        /// <summary>
        /// 由插件完全接管的还原流程
        /// </summary>
        private static async Task<bool> HandlePluginRestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem historyItem,
            BackupTask task,
            Services.Plugins.IFolderRewindPlugin plugin,
            int configIndex)
        {
            Log(I18n.Format("BackupService_Log_PluginRestoreTakeover", plugin.Manifest.Name, folder.DisplayName), LogLevel.Info);

            await RunOnUIAsync(() =>
            {
                task.Status = I18n.Format("BackupService_Task_PluginProcessing");
                task.Progress = 0;
                task.IsIndeterminate = true;
            });

            bool restoreStarted = false;

            try
            {
                BroadcastRestoreLifecycle("command_started");
                BroadcastRestoreEvent(configIndex, config, folder, "restore_started");
                restoreStarted = true;

                var result = await Services.Plugins.PluginService.InvokePluginRestoreAsync(
                    plugin,
                    config,
                    folder,
                    historyItem.FileName,
                    async (progress, status) =>
                    {
                        await RunOnUIAsync(() =>
                        {
                            task.Progress = progress;
                            task.Status = string.IsNullOrWhiteSpace(status)
                                ? I18n.Format("BackupService_Task_PluginProcessing")
                                : status;
                            task.IsIndeterminate = false;
                        });
                    });

                if (result.Success)
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.GetString("BackupService_Task_RestoreCompleted");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;
                        task.ErrorMessage = string.Empty;
                    });

                    Log(I18n.Format("BackupService_Log_PluginRestoreSucceeded", folder.DisplayName), LogLevel.Info);
                    NotificationService.NotifyRestoreCompleted(folder.DisplayName, true, I18n.GetString("BackupService_Task_RestoreCompleted"));

                    BroadcastRestoreEvent(configIndex, config, folder, "restore_success", new Dictionary<string, string?>
                    {
                        ["backup"] = historyItem.FileName
                    });
                    BroadcastRestoreLifecycle("command_completed", new Dictionary<string, string?>
                    {
                        ["backup"] = historyItem.FileName
                    });

                    return true;
                }

                string failureMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? I18n.GetString("BackupService_Task_RestoreFailed")
                    : result.Message!;

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("BackupService_Task_RestoreFailed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = failureMessage;
                });

                Log(I18n.Format("BackupService_Log_PluginRestoreFailed", folder.DisplayName, failureMessage), LogLevel.Error);

                if (restoreStarted)
                {
                    BroadcastRestoreLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "plugin_restore_failed" });
                    BroadcastRestoreEvent(configIndex, config, folder, "restore_finished", new Dictionary<string, string?>
                    {
                        ["status"] = "failure",
                        ["reason"] = "plugin_restore_failed"
                    });
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, failureMessage);
                return false;
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("BackupService_Task_RestoreFailed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);

                if (restoreStarted)
                {
                    BroadcastRestoreLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "plugin_restore_exception" });
                    BroadcastRestoreEvent(configIndex, config, folder, "restore_finished", new Dictionary<string, string?>
                    {
                        ["status"] = "failure",
                        ["reason"] = "plugin_restore_exception"
                    });
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, ex.Message);
                return false;
            }
        }

    }
}
