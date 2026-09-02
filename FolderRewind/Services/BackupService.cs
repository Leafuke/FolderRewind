using FolderRewind.Models;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Application;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Abstractions;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// 源/目标路径重叠检查、按压缩模式分发、Native History 事务提交与云端同步排队，
    /// 以及备份后的保留期清理（KeepCount）。全部为静态成员，由 UI、自动化调度、
    /// KnotLink 远程命令和插件宿主共同调用。
    /// </summary>
    public static partial class BackupService
    {
        public static ObservableCollection<BackupTask> ActiveTasks { get; } = new();

        // 还原阶段会用内部标记目录记录“仅删除”动作，完成后必须清理避免污染用户目录。
        internal const string InternalRestoreMarkerDirectoryName = "__FolderRewind_Internal";
        internal const string InternalRestoreMarkerFileName = "__DeletedOnly.marker";
        private static string MissingEncryptionPasswordMessage =>
            I18n.GetString("BackupService_MissingEncryptionPassword");

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

        // 单个备份源的执行结果：同时供 Native RunVersion、插件结果映射与 UI 状态展示消费。
        internal sealed class BackupSourceExecutionOutcome
        {
            public BackupSourceExecutionStatus Status { get; init; }
            public Guid? FolderId { get; init; }
            public string FolderPath { get; init; } = string.Empty;
            public string FolderName { get; init; } = string.Empty;
            public string ErrorMessage { get; init; } = string.Empty;
            public OperationOutcome OperationOutcome { get; init; } = OperationOutcome.Failed;
            public SourceCaptureResult? CaptureResult { get; init; }
            public string? GeneratedFileName { get; init; }
            public BackupTask? Task { get; init; }
            public bool CreatedNewArchive => Status == BackupSourceExecutionStatus.NewArchive;

            public PluginBackupRequestResult ToPluginResult()
                => PluginBackupRequestResult.FromSource(Status, OperationOutcome);
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

            var result = await ExecuteBackupTransactionAsync(
                config,
                config.SourceFolders,
                invocationOptions,
                HistoryCommitIntent.AdvanceBranch,
                safetySnapshotIntent: null,
                CancellationToken.None).ConfigureAwait(false);

            Log(I18n.Format("BackupService_Log_TaskEnd"), LogLevel.Info);
            return result.CreatedNewArchive;
        }

        internal static async Task<SafetySnapshot> CreateSafetySnapshotAsync(
            BackupConfig config,
            SafetySnapshotReason reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            var options = BackupInvocationOptions.ForInternal();
            var result = await ExecuteBackupTransactionAsync(
                config,
                config.SourceFolders,
                options,
                HistoryCommitIntent.IndependentRecoveryPoint,
                new HistorySafetySnapshotIntent(reason),
                cancellationToken).ConfigureAwait(false);

            return result.SafetySnapshot
                ?? result.CommittedBatch?.NewSafetySnapshot
                ?? throw new InvalidOperationException("Independent recovery commit did not create a SafetySnapshot.");
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
                CancellationToken.None).ConfigureAwait(false);
            return outcome.CreatedNewArchive;
        }

        /// <summary>
        /// 插件备份请求入口：包装统一备份事务编排并把结果映射为插件的
        /// <see cref="OperationOutcome"/>，仅在产生新归档时触发保留期清理；
        /// 取消与异常均转换为 Canceled/Failed 结果返回，不向插件抛出。
        /// </summary>
        internal static async Task<PluginBackupRequestResult> BackupFolderForPluginAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions invocationOptions,
            CancellationToken cancellationToken)
        {
            if (NativeHostMutationContext.IsNestedMutationBlocked)
                return new PluginBackupRequestResult(OperationOutcome.Blocked, CreatedNewArchive: false);
            if (config is null || folder is null)
                return new PluginBackupRequestResult(OperationOutcome.Blocked, CreatedNewArchive: false);

            try
            {
                var result = await ExecuteBackupTransactionAsync(
                    config,
                    [folder],
                    invocationOptions,
                    HistoryCommitIntent.AdvanceBranch,
                    safetySnapshotIntent: null,
                    cancellationToken).ConfigureAwait(false);

                return new PluginBackupRequestResult(result.Outcome, result.CreatedNewArchive);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new PluginBackupRequestResult(OperationOutcome.Canceled, CreatedNewArchive: false);
            }
            catch (Exception ex)
            {
                Log($"Plugin folder backup request failed: {ex.Message}", LogLevel.Error);
                return new PluginBackupRequestResult(OperationOutcome.Failed, CreatedNewArchive: false);
            }
        }

        internal static async Task<PluginBackupRequestResult> BackupConfigurationForPluginAsync(
            BackupConfig config,
            IReadOnlyList<ManagedFolder> requestedFolders,
            BackupInvocationOptions invocationOptions,
            CancellationToken cancellationToken)
        {
            if (NativeHostMutationContext.IsNestedMutationBlocked)
                return new PluginBackupRequestResult(OperationOutcome.Blocked, CreatedNewArchive: false);
            if (config is null || requestedFolders is null || requestedFolders.Count == 0)
                return new PluginBackupRequestResult(OperationOutcome.Blocked, CreatedNewArchive: false);

            try
            {
                var result = await ExecuteBackupTransactionAsync(
                    config,
                    requestedFolders,
                    invocationOptions,
                    HistoryCommitIntent.AdvanceBranch,
                    safetySnapshotIntent: null,
                    cancellationToken).ConfigureAwait(false);

                return new PluginBackupRequestResult(result.Outcome, result.CreatedNewArchive);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new PluginBackupRequestResult(OperationOutcome.Canceled, CreatedNewArchive: false);
            }
            catch (Exception ex)
            {
                Log($"Plugin config backup request failed: {ex.Message}", LogLevel.Error);
                return new PluginBackupRequestResult(OperationOutcome.Failed, CreatedNewArchive: false);
            }
        }

        /// <summary>
        /// 执行单个备份源的捕获阶段（Capture）：
        /// 消费已解析的有效策略，获取 consistency lease、校验路径、差异计算、归档生成及校验与元数据捕获。
        /// 关键设计：本阶段只负责捕获产物，严禁在 Native History 提交前广播成功或更新 LastBackupTime。
        /// </summary>
        private static async Task<BackupSourceExecutionOutcome> CaptureBackupSourceAsync(
            BackupConfig config,
            ManagedFolder folder,
            PluginV3BackupSourceResolution resolution,
            string? comment,
            BackupInvocationOptions? invocationOptions,
            CancellationToken cancellationToken = default)
        {
            if (config == null || folder == null)
            {
                return new BackupSourceExecutionOutcome
                {
                    Status = BackupSourceExecutionStatus.Failed,
                    ErrorMessage = I18n.GetString("BackupService_InvalidConfigurationOrSource")
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

            await using var v3Session = PluginV3BackupSession.Create(resolution);
            if (v3Session.IsBlocked)
            {
                var diagnostic = v3Session.Diagnostics.LastOrDefault();
                var message = diagnostic is null
                    ? "The v3 plugin policy blocked this backup."
                    : diagnostic.Code == NativeHistoryArtifactTransformPolicy.BlockedDiagnosticCode
                        ? I18n.GetString("BackupService_ArtifactTransformNativeHistoryNotSupported")
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
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: message,
                    operationOutcome: OperationOutcome.Blocked,
                    task: task);
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
                return CreateSourceOutcome(folder, BackupSourceExecutionStatus.Failed, errorMessage: filterValidationError, task: task);
            }

            var effectiveBoundary = resolution.EffectiveBoundary;

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

                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: I18n.Format("BackupService_Folder_TargetNotSet"),
                    task: task);
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

                return CreateSourceOutcome(folder, BackupSourceExecutionStatus.Failed, errorMessage: invalidFolderNameMessage, task: task);
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
                return CreateSourceOutcome(folder, BackupSourceExecutionStatus.Failed, errorMessage: overlapMessage, task: task);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: I18n.GetString("Common_Canceled"),
                    operationOutcome: OperationOutcome.Canceled,
                    task: task);
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
                return CreateSourceOutcome(folder, BackupSourceExecutionStatus.Failed, errorMessage: ex.Message, task: task);
            }

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

                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Unavailable,
                    errorMessage: I18n.Format("BackupService_Folder_SourceNotFound"),
                    task: task);
            }

            if (!Directory.Exists(backupSubDir)) Directory.CreateDirectory(backupSubDir);

            Log(I18n.Format("BackupService_Log_ProcessingFolder", folder.DisplayName), LogLevel.Info);
            await RunOnUIAsync(() => folder.StatusText = I18n.Format("BackupService_Folder_BackupInProgress"));

            BroadcastBackupEvent(configIndex, config, folder, "backup_started");

            bool success = false;
            bool canceled = false;
            bool sourceUnavailable = false;
            string? generatedFileName = null;
            SourceCaptureResult? captureResult = null;
            var sourceId = new SourceId(Guid.Parse(folder.Id));
            SourceCaptureBaseline? captureBaseline;
            try
            {
                captureBaseline = await NativeHistoryCoreGateway.LoadCaptureBaselineAsync(
                    config.Id,
                    sourceId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: I18n.GetString("Common_Canceled"),
                    operationOutcome: OperationOutcome.Canceled,
                    task: task);
            }
            if (captureBaseline is not null
                && !StringComparer.Ordinal.Equals(
                    captureBaseline.BoundaryFingerprint,
                    effectiveBoundary.Fingerprint))
            {
                captureBaseline = null;
            }
            var captureScope = CaptureScopePolicy.Determine(effectiveBoundary, effectiveBoundary);
            try
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Processing");
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupRunning");
                });

                switch (config.Archive.Mode)
                {
                    case BackupMode.Smart:
                        {
                            var res = await DoSmartBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, captureBaseline, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Rolling:
                        {
                            var res = await DoRollingBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, captureBaseline, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Full:
                    default:
                        {
                            var res = await DoFullBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, captureBaseline, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                }
                captureResult = captureResult?.WithEffectiveSourceBoundary(effectiveBoundary);
                if (captureResult?.Outcome == SourceCaptureOutcome.Captured)
                {
                    var metadata = await v3Session.CaptureVersionMetadataAsync(cancellationToken).ConfigureAwait(false);
                    captureResult = captureResult.WithVersionMetadata(
                        metadata.Candidates,
                        metadata.Diagnostics);
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

            if (sourceUnavailable)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Unavailable,
                    errorMessage: I18n.GetString("BackupService_Folder_NoMatchingFiles"),
                    captureResult: captureResult,
                    task: task);
            }
            if (!success)
            {
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: task.ErrorMessage,
                    captureResult: captureResult,
                    operationOutcome: canceled ? OperationOutcome.Canceled : OperationOutcome.Failed,
                    task: task);
            }
            if (!string.IsNullOrWhiteSpace(generatedFileName))
            {
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.NewArchive,
                    captureResult: captureResult,
                    operationOutcome: completionOutcome,
                    generatedFileName: generatedFileName,
                    task: task);
            }

            return CreateSourceOutcome(
                folder,
                BackupSourceExecutionStatus.Reused,
                captureResult: captureResult,
                task: task);
        }

        private static BackupSourceExecutionOutcome CreateSourceOutcome(
            ManagedFolder folder,
            BackupSourceExecutionStatus status,
            string? errorMessage = null,
            SourceCaptureResult? captureResult = null,
            OperationOutcome? operationOutcome = null,
            string? generatedFileName = null,
            BackupTask? task = null) => new()
        {
            Status = status,
            FolderId = Guid.TryParse(folder.Id, out var folderId) ? folderId : null,
            FolderPath = folder.Path ?? string.Empty,
            FolderName = folder.DisplayName ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            CaptureResult = captureResult,
            OperationOutcome = operationOutcome ?? PluginBackupRequestResult.FromSource(status).Outcome,
            GeneratedFileName = generatedFileName,
            Task = task
        };

        private static SourceCaptureResult EnsureCaptureResult(
            ManagedFolder folder,
            EffectiveSourceBoundarySnapshot authoritativeBoundary,
            BackupSourceExecutionOutcome outcome)
        {
            if (outcome.CaptureResult is not null) return outcome.CaptureResult;
            var sourceId = Guid.TryParse(folder.Id, out var parsed) && parsed != Guid.Empty
                ? new SourceId(parsed)
                : throw new InvalidDataException("ManagedFolder has no stable SourceId.");
            var scope = FolderRewind.History.Domain.CaptureScope.FullSource;
            if (outcome.Status == BackupSourceExecutionStatus.Unavailable)
                return SourceCaptureResult.Unavailable(sourceId, scope, outcome.ErrorMessage)
                    .WithEffectiveSourceBoundary(authoritativeBoundary);
            var captureOutcome = outcome.OperationOutcome switch
            {
                OperationOutcome.Blocked => SourceCaptureOutcome.Blocked,
                OperationOutcome.Canceled => SourceCaptureOutcome.Canceled,
                _ => SourceCaptureOutcome.Failed
            };
            var diagnostic = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
                ? Array.Empty<HistoryDiagnostic>()
                : [new HistoryDiagnostic(
                    "capture.failed",
                    HistoryDiagnosticSeverity.Error,
                    outcome.ErrorMessage)];
            return new SourceCaptureResult(
                sourceId,
                captureOutcome,
                scope,
                stateFingerprint: null,
                existingVersionId: null,
                representationCandidate: null,
                localReplicaCandidate: null,
                payloadCandidate: null,
                expectedWorkspaceRevision: -1,
                expectedBaseVersionId: null,
                cleanupHandle: null,
                diagnostics: diagnostic,
                effectiveSourceBoundary: authoritativeBoundary);
        }

        private static async Task CleanupUncommittedOutcomesAsync(
            IEnumerable<BackupSourceExecutionOutcome> outcomes)
        {
            foreach (var cleanup in outcomes
                         .Select(item => item.CaptureResult?.CleanupHandle)
                         .Where(item => item is not null))
            {
                try { await cleanup!.CleanupAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }

        private static SourceCaptureResult CreateArchiveCapture(
            SourceId sourceId,
            FolderRewind.History.Domain.CaptureScope captureScope,
            string destinationDirectory,
            string fileName,
            RepresentationKind kind,
            string format,
            IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
            SourceCaptureBaseline? baseline,
            IEnumerable<RepresentationId> dependencies,
            int consecutiveSmartCaptures,
            VersionId? expectedBaseVersionId = null,
            IEnumerable<string>? deletedFiles = null)
            => VerifiedArchiveCaptureFactory.Create(
                sourceId,
                captureScope,
                Path.Combine(destinationDirectory, fileName),
                kind,
                format,
                currentStates,
                baseline,
                dependencies,
                consecutiveSmartCaptures,
                captureScope == FolderRewind.History.Domain.CaptureScope.FullSource
                    || (kind == RepresentationKind.CoreSmartDelta && expectedBaseVersionId is not null)
                    ? MaterializationFidelity.Exact
                    : MaterializationFidelity.Partial,
                expectedBaseVersionId,
                deletedFiles);

        public enum RestoreMode
        {
            Clean = 0,
            Overwrite = 1
        }

        private static BackupInvocationKind MapInvocationKind(BackupInvocationSource source) => source switch
        {
            BackupInvocationSource.Automatic => BackupInvocationKind.Automatic,
            BackupInvocationSource.Remote => BackupInvocationKind.Remote,
            BackupInvocationSource.PluginHotkey => BackupInvocationKind.PluginHotkey,
            BackupInvocationSource.Internal => BackupInvocationKind.Internal,
            _ => BackupInvocationKind.Manual
        };

        private static OperationOutcome CombineSuccessfulBackupOutcomes(
            OperationOutcome current,
            OperationOutcome next)
            => current == OperationOutcome.SuccessWithWarnings || next == OperationOutcome.SuccessWithWarnings
                ? OperationOutcome.SuccessWithWarnings
                : next;

        private static async Task PruneRetainedSourceArchivesAsync(BackupConfig config)
        {
            if (config.Archive.KeepCount <= 0)
            {
                return;
            }
            await NativeHistoryApplicationService.ApplyAutomaticRetentionAsync(config);
        }

    }
}
