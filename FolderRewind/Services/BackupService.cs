using FolderRewind.Models;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Legacy;
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
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 备份引擎核心：编排单个备份源的完整生命周期——插件 v3 一致性会话、过滤校验、
    /// 源/目标路径重叠检查、按压缩模式分发、产物事务提交、历史条目写入与云端同步排队，
    /// 以及备份后的保留期清理（KeepCount）。全部为静态成员，由 UI、自动化调度、
    /// KnotLink 远程命令和插件宿主共同调用。
    /// </summary>
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

        // 单个备份源的执行结果：同时供备份运行记录、插件结果映射与 UI 状态展示三处消费。
        private sealed class BackupSourceExecutionOutcome
        {
            public BackupRunSourceStatus Status { get; init; }
            public Guid? FolderId { get; init; }
            public string FolderPath { get; init; } = string.Empty;
            public string FolderName { get; init; } = string.Empty;
            public HistoryItem? HistoryItem { get; init; }
            public string ErrorMessage { get; init; } = string.Empty;
            public OperationOutcome OperationOutcome { get; init; } = OperationOutcome.Failed;
            public SourceCaptureResult? CaptureResult { get; init; }
            public bool CreatedNewArchive => Status == BackupRunSourceStatus.NewArchive;

            public PluginBackupRequestResult ToPluginResult()
                => PluginBackupRequestResult.FromSource(Status, OperationOutcome);

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
                LegacyHistoryCapturePersistenceAdapter.PersistRun(config, run);
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
            var outcome = await BackupFolderForPluginAsync(
                config,
                folder,
                invocationOptions,
                CancellationToken.None);
            return outcome.CreatedNewArchive;
        }

        /// <summary>
        /// 插件备份请求入口：包装 <see cref="BackupFolderCoreAsync"/> 并把结果映射为插件的
        /// <see cref="OperationOutcome"/>，仅在产生新归档时触发保留期清理；
        /// 取消与异常均转换为 Canceled/Failed 结果返回，不向插件抛出。
        /// </summary>
        internal static async Task<PluginBackupRequestResult> BackupFolderForPluginAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions invocationOptions,
            CancellationToken cancellationToken)
        {
            try
            {
                var outcome = await BackupFolderCoreAsync(
                    config,
                    folder,
                    invocationOptions.Comment,
                    invocationOptions,
                    createdByRunId: null,
                    cancellationToken);
                if (outcome.CreatedNewArchive)
                {
                    await PruneRetainedSourceArchivesAsync(config);
                }
                return outcome.ToPluginResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new PluginBackupRequestResult(OperationOutcome.Canceled, CreatedNewArchive: false);
            }
            catch (Exception ex)
            {
                Log($"Plugin backup request failed: {ex.Message}", LogLevel.Error);
                return new PluginBackupRequestResult(OperationOutcome.Failed, CreatedNewArchive: false);
            }
        }

        /// <summary>
        /// 执行单个备份源的完整流程：插件 v3 会话准备与一致性租约获取、过滤规则与目标路径校验、
        /// 源/目标路径重叠检查、按压缩模式分发到 DoSmart/DoRolling/DoFullBackupAsync，
        /// 成功后提交产物事务、写入历史条目、排队云端上传，并触发完成观察者。
        /// </summary>
        /// <remarks>
        /// 结果三态：<c>Unavailable</c> 表示源当前没有匹配文件（可预期缺席，不算失败）；
        /// <c>Failed</c> 内部再区分用户取消（Canceled）与真实失败；
        /// 未生成新归档且无变化时复用最近一条历史条目（Reused）。
        /// 一致性租约只覆盖源校验、差异计算与归档创建；产物与历史落盘之后，
        /// 完成观察者被取消只会把结果降级为 SuccessWithWarnings，不会改写已持久化的备份结果。
        /// </remarks>
        private static async Task<BackupSourceExecutionOutcome> BackupFolderCoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            string? comment,
            BackupInvocationOptions? invocationOptions,
            string? createdByRunId,
            CancellationToken cancellationToken = default)
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
            cancellationToken.ThrowIfCancellationRequested();

            int configIndex = GetConfigIndex(config);

            // 1. 创建任务对象并确保在 UI 线程添加到集合
            var task = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = I18n.Format("BackupService_Task_Preparing"),
                Progress = 0
            };

            await RunOnUIAsync(() => ActiveTasks.Insert(0, task));

            await using var v3Session = await PluginV3BackupSession.PrepareAsync(
                config,
                folder,
                cancellationToken);
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
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Failed,
                    errorMessage: message,
                    operationOutcome: OperationOutcome.Blocked);
            }

            var runtimeConfig = v3Session.EffectiveConfig;
            var runtimeFolder = v3Session.EffectiveFolder;

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
                await v3Session.AcquireConsistencyAsync(cancellationToken);
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

            // v3 一致性租约负责快照和源路径替换，Host 始终掌握归档、历史及清理生命周期。
            // 允许插件在备份前创建快照并替换源路径（例如 Minecraft 热备份：先复制到 snapshot 再备份）。
            string sourcePath = v3Session.SourcePath;
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

            // 三态结果标志：success=归档创建成功；canceled=用户取消（区别于失败）；
            // sourceUnavailable=源目录当前没有匹配文件（可预期缺席）。
            bool success = false;
            bool canceled = false;
            bool sourceUnavailable = false;
            string? generatedFileName = null;
            HistoryItem? generatedHistoryItem = null;
            SourceCaptureResult? captureResult = null;
            var sourceId = new SourceId(Guid.Parse(folder.Id));
            var captureScope = IsPartialBackupFilter(runtimeConfig.Filters) || runtimeFolder.SourceScope.IsPartial
                ? FolderRewind.History.Domain.CaptureScope.PartialSource
                : FolderRewind.History.Domain.CaptureScope.FullSource;
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
                    case BackupMode.Smart:
                        {
                            var res = await DoSmartBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, metadataDir, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Rolling:
                        {
                            var res = await DoRollingBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, metadataDir, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Full:
                    default:
                        {
                            var res = await DoFullBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, metadataDir, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                success = false;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_Exception", ex.Message), LogLevel.Error);
                success = false;
                await RunOnUIAsync(() => { if (string.IsNullOrEmpty(task.ErrorMessage)) task.ErrorMessage = ex.Message; });
            }

            // The consistency lease spans source validation, diff calculation and
            // archive creation, but cleanup finishes before History/Cloud commit.
            var operationDiagnostics = v3Session.Diagnostics.ToList();
            var completionOutcome = operationDiagnostics.Any(value => value.Severity == DiagnosticSeverity.Warning)
                ? OperationOutcome.SuccessWithWarnings
                : OperationOutcome.Success;
            try
            {
                await v3Session.CompleteCaptureAsync();
            }
            catch (Exception ex)
            {
                completionOutcome = OperationOutcome.SuccessWithWarnings;
                Log($"[PluginV3] Consistency cleanup failed: {ex.Message}", LogLevel.Warning);
                operationDiagnostics.Add(new PluginDiagnostic(
                    "plugin.backup_consistency_cleanup_failed",
                    DiagnosticSeverity.Warning,
                    "BackupConsistency",
                    v3Session.EffectiveConfig.Kind.OwnerId,
                    new Dictionary<string, string> { ["message"] = ex.Message }));
            }

            PluginV3ArtifactCommitResult? artifactCommit = null;
            string? pendingHistoryItemId = null;
            if (success && !sourceUnavailable && !string.IsNullOrWhiteSpace(generatedFileName))
            {
                try
                {
                    // 预生成历史条目 ID，使产物事务与稍后创建的历史条目共享同一标识。
                    pendingHistoryItemId = Guid.NewGuid().ToString("N");
                    artifactCommit = await PluginV3ArtifactService.CommitBackupAsync(
                        config,
                        folder,
                        pendingHistoryItemId,
                        Path.Combine(backupSubDir, generatedFileName),
                        generatedFileName,
                        IsPartialBackupFilter(runtimeConfig.Filters) || runtimeFolder.SourceScope.IsPartial,
                        operationDiagnostics,
                        cancellationToken);
                    completionOutcome = CombineSuccessfulBackupOutcomes(
                        completionOutcome,
                        artifactCommit.Outcome);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    success = false;
                    await RunOnUIAsync(() => task.ErrorMessage = "Backup request was canceled.");
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
                    if (runtimeConfig.Archive.Mode == BackupMode.Smart)
                    {
                        typeStr = completedFileName.StartsWith("[Full]", StringComparison.OrdinalIgnoreCase) ? "Full" : "Smart";
                    }
                    else
                    {
                        typeStr = runtimeConfig.Archive.Mode.ToString();
                    }
                    generatedHistoryItem = LegacyHistoryCapturePersistenceAdapter.AddEntry(
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
                        PluginV3ModelMapper.ToPersisted(completionOutcome),
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
                        try
                        {
                            var observerOutcome = await PluginV3ArtifactService.ObserveCompletionAsync(
                                createdByRunId ?? generatedHistoryItem.Id,
                                config,
                                folder,
                                generatedHistoryItem,
                                artifactCommit,
                                cancellationToken);
                            completionOutcome = CombineSuccessfulBackupOutcomes(
                                completionOutcome,
                                observerOutcome);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // Core Artifact/History is already durable; observer cancellation cannot rewrite it as failure.
                            completionOutcome = OperationOutcome.SuccessWithWarnings;
                            Log("[PluginV3] Completion observer was canceled after the backup committed.", LogLevel.Warning);
                            var observerCanceled = new PluginDiagnostic(
                                "plugin.backup_observer_canceled_after_commit",
                                DiagnosticSeverity.Warning,
                                "BackupCompletionObserver",
                                v3Session.EffectiveConfig.Kind.OwnerId,
                                new Dictionary<string, string>());
                            LegacyHistoryCapturePersistenceAdapter.ApplyOperationResult(
                                generatedHistoryItem.Id,
                                PersistedOperationOutcome.SuccessWithWarnings,
                                artifactCommit.Diagnostics
                                    .Append(observerCanceled)
                                    .Select(PluginV3ModelMapper.ToRecord)
                                    .ToArray());
                        }
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

            if (sourceUnavailable)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Unavailable,
                    errorMessage: I18n.GetString("BackupService_Folder_NoMatchingFiles"),
                    captureResult: captureResult);
            }
            if (!success)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Failed,
                    errorMessage: task.ErrorMessage,
                    captureResult: captureResult,
                    operationOutcome: canceled ? OperationOutcome.Canceled : OperationOutcome.Failed);
            }
            if (!string.IsNullOrWhiteSpace(generatedFileName))
            {
                return CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.NewArchive,
                    generatedHistoryItem,
                    captureResult: captureResult,
                    operationOutcome: completionOutcome);
            }

            // 无变化且未生成新归档：复用最近一条历史条目，让运行记录仍能指向可恢复的条目。
            var reusedHistory = LegacyHistoryCapturePersistenceAdapter.GetLatestEntry(config, folder);
            return reusedHistory == null
                ? CreateSourceOutcome(
                    folder,
                    BackupRunSourceStatus.Unavailable,
                    errorMessage: "No changes were detected, but no previous successful history item exists.",
                    captureResult: captureResult)
                : CreateSourceOutcome(folder, BackupRunSourceStatus.Reused, reusedHistory, captureResult: captureResult);
        }

        private static BackupSourceExecutionOutcome CreateSourceOutcome(
            ManagedFolder folder,
            BackupRunSourceStatus status,
            HistoryItem? historyItem = null,
            string? errorMessage = null,
            SourceCaptureResult? captureResult = null,
            OperationOutcome? operationOutcome = null) => new()
        {
            Status = status,
            FolderId = Guid.TryParse(folder.Id, out var folderId) ? folderId : null,
            FolderPath = folder.Path ?? string.Empty,
            FolderName = folder.DisplayName ?? string.Empty,
            HistoryItem = historyItem,
            ErrorMessage = errorMessage ?? string.Empty,
            CaptureResult = captureResult,
            OperationOutcome = operationOutcome ?? PluginBackupRequestResult.FromSource(status).Outcome
        };

        private static SourceCaptureResult CreateLegacyArchiveCapture(
            SourceId sourceId,
            FolderRewind.History.Domain.CaptureScope captureScope,
            string destinationDirectory,
            string fileName,
            RepresentationKind kind,
            string format,
            CapturePayloadState payloadState = CapturePayloadState.FinalUnverified)
        {
            var representationId = RepresentationId.New();
            var absolutePath = Path.GetFullPath(Path.Combine(destinationDirectory, fileName));
            var payload = new CapturePayloadCandidate(
                absolutePath,
                payloadState,
                File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : null,
                ExpectedStorageSha256: null);
            var representation = new RepresentationCandidate(
                representationId,
                kind,
                format,
                dependencyRepresentationIds: [],
                captureScope == FolderRewind.History.Domain.CaptureScope.PartialSource
                    ? FolderRewind.History.Domain.RestoreStrategy.Overlay
                    : FolderRewind.History.Domain.RestoreStrategy.Exact,
                logicalSha256: null,
                stateFingerprint: null,
                metadata: null,
                isLegacyBridgeCandidate: true);
            var localReplica = new LocalReplicaCandidate(
                LocalReplicaId.New(),
                representationId,
                LocalReplicaLocator.ControlledAbsolute(absolutePath),
                payloadState,
                DateTimeOffset.UtcNow);
            return new SourceCaptureResult(
                sourceId,
                SourceCaptureOutcome.Captured,
                captureScope,
                stateFingerprint: null,
                existingVersionId: null,
                representation,
                localReplica,
                payload,
                expectedWorkspaceRevision: -1,
                expectedBaseVersionId: null,
                cleanupHandle: null,
                diagnostics: []);
        }

        private static BackupRunTriggerSource MapRunTriggerSource(BackupInvocationSource source) => source switch
        {
            BackupInvocationSource.Manual => BackupRunTriggerSource.Manual,
            BackupInvocationSource.Automatic => BackupRunTriggerSource.Automatic,
            BackupInvocationSource.Remote => BackupRunTriggerSource.Remote,
            BackupInvocationSource.PluginHotkey => BackupRunTriggerSource.PluginHotkey,
            BackupInvocationSource.Internal => BackupRunTriggerSource.Internal,
            _ => BackupRunTriggerSource.Unknown
        };

        private static OperationOutcome CombineSuccessfulBackupOutcomes(
            OperationOutcome current,
            OperationOutcome next)
            => current == OperationOutcome.SuccessWithWarnings || next == OperationOutcome.SuccessWithWarnings
                ? OperationOutcome.SuccessWithWarnings
                : next;

        /// <summary>
        /// 备份完成后按 KeepCount 保留策略清理多余的源归档：先委托 <see cref="BackupRunPolicy"/>
        /// 计算可删除的历史条目（保护 Important 条目与被保留运行引用的条目），
        /// 再逐个走 <see cref="DeleteBackupAsync"/> 的安全删除路径；单个清理失败仅记警告。
        /// </summary>
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

        /// <summary>
        /// 删除一条备份运行记录：仅移除运行分组本身，不触碰其引用的历史条目与归档；
        /// 删除后重新执行保留期清理，并排队云端配置历史同步。
        /// </summary>
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

    }
}
