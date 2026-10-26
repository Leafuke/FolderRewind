using FolderRewind.Models;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Application;
using FolderRewind.History.Storage;
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
        internal static Task<BackupOperationResult> CreateSemanticCheckpointAsync(BackupConfig config, CancellationToken token)
            => CreateSemanticCheckpointAsync(config, new(config, config.Id, config.ConfigRevision, config.SourceFolders.Select(f => f.Id)), token);

        internal static async Task<BackupOperationResult> CreateSemanticCheckpointAsync(BackupConfig config, BackupConfigurationHandoff handoff, CancellationToken token)
        {
            var result = await ExecuteBackupTransactionAsync(config, config.SourceFolders,
                new BackupInvocationOptions { Source = BackupInvocationSource.Manual, PreferApplicationConsistentSnapshot = true,
                    Comment = I18n.GetString("SemanticBackup_CheckpointHistoryComment"), RequestSemanticCheckpoint = true, ConfigurationHandoff = handoff },
                HistoryCommitIntent.AdvanceBranch, cancellationToken: token).ConfigureAwait(false);
            return new(result.Outcome, result.CreatedNewArchive, result.HistoryRecoveryRequired);
        }
        internal static BackupTaskCollection TaskRegistry { get; } = new();
        public static ObservableCollection<BackupTask> ActiveTasks => TaskRegistry;

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

            public ScopeAwareSourceCaptureDiff ToCaptureDiff() => new([.. AddedFiles], [.. ModifiedFiles], [.. DeletedFiles]);
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
            public OperationOutcome OperationOutcome { get; set; } = OperationOutcome.Failed;
            public SourceCaptureResult? CaptureResult { get; init; }
            public IReadOnlyList<PluginDiagnostic> WarningDiagnostics { get; set; } = [];
            public IReadOnlyList<string> WarningCodes => WarningDiagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal).ToArray();
            internal HashSet<string> PublishedWarningIdentities { get; } = new(StringComparer.Ordinal);
            internal bool WarningNotificationSent { get; set; }
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



        /// <summary>
        /// 备份配置下的所有文件夹
        /// </summary>
        /// <returns>保留事务状态、新归档标志与恢复待处理状态。</returns>
        public static async Task<BackupOperationResult> BackupConfigAsync(
            BackupConfig config,
            BackupInvocationOptions? invocationOptions = null)
        {
            if (config == null) return new(OperationOutcome.Blocked, false);
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
            return new(result.Outcome, result.CreatedNewArchive, result.HistoryRecoveryRequired);
        }

        internal static async Task<SafetySnapshot> CreateSafetySnapshotAsync(
            BackupConfig config,
            SafetySnapshotReason reason,
            CancellationToken cancellationToken = default,
            NativeHistoryConfigurationOperationGate.Lease? existingOperation = null,
            System.Collections.Generic.IReadOnlyCollection<SourceId>? affectedSources = null,
            System.Collections.Generic.IReadOnlyList<HistoryRestoreSourceBinding>? physicalBindings = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            var options = BackupInvocationOptions.ForSafetySnapshot(physicalBindings);
            var sources = config.SourceFolders.Where(f => affectedSources is null || affectedSources.Contains(new SourceId(Guid.Parse(f.Id)))).ToArray();
            if (options.PhysicalSourceBindings is { } bindings && sources.Any(f => !bindings.ContainsKey(new SourceId(Guid.Parse(f.Id)))))
                throw new InvalidOperationException("Safety snapshot requires a physical binding for every affected Source.");
            var result = await ExecuteBackupTransactionAsync(
                config,
                sources,
                options,
                HistoryCommitIntent.IndependentRecoveryPoint,
                new HistorySafetySnapshotIntent(reason),
                cancellationToken, existingOperation).ConfigureAwait(false);

            var snapshot = result.SafetySnapshot
                ?? result.CommittedBatch?.NewSafetySnapshot
                ?? throw new InvalidOperationException("Independent recovery commit did not create a SafetySnapshot: " + result.Outcome + "; "
                    + (result.Diagnostic ?? result.RecoveryMessage ?? string.Join("; ", result.SourceOutcomes.Select(o => o.ErrorMessage))));
            // 每个载荷已按配置的备份验证等级在入库前验证；提交后不再无条件完整重验。
            return snapshot;
        }

        /// <summary>
        /// 备份单个文件夹
        /// </summary>
        /// <returns>保留事务状态、新归档标志与恢复待处理状态。</returns>
        public static async Task<BackupOperationResult> BackupFolderAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions? invocationOptions = null)
        {
            if (config is null || folder is null || NativeHostMutationContext.IsNestedMutationBlocked)
                return new(OperationOutcome.Blocked, false);
            var result = await ExecuteBackupTransactionAsync(
                config, [folder], invocationOptions ?? BackupInvocationOptions.Default,
                HistoryCommitIntent.AdvanceBranch, safetySnapshotIntent: null,
                CancellationToken.None).ConfigureAwait(false);
            return new(result.Outcome, result.CreatedNewArchive, result.HistoryRecoveryRequired);
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
            HistoryRuntime operationRuntime,
            NativeHistoryConfigurationOperationGate.Lease operationLease,
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
                        : ExperimentalArtifactPolicyService.Describe(diagnostic);
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

            string sourcePath = v3Session.SourcePath;
            var physicalBinding = invocationOptions.PhysicalSourceBindings?.GetValueOrDefault(new SourceId(Guid.Parse(folder.Id)));
            using var physicalSource = physicalBinding is not null && !NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig)
                ? new WindowsPhysicalDirectoryLease(physicalBinding.TargetDirectory) : null;
            if (physicalSource is not null)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(runtimeFolder.Path), Path.GetFullPath(physicalBinding!.ComparisonRoot))
                    || physicalBinding.Boundary.Fingerprint != effectiveBoundary.Fingerprint
                    || !StringComparer.OrdinalIgnoreCase.Equals(HistoryRestoreWorkspacePlanner.ResolvePath(runtimeFolder.Path), physicalSource.PhysicalPath))
                    throw new IOException("Safety snapshot Source binding changed during restore preparation.");
                // 插件提供的稳定视图仍由其租约负责；直接读取目标时使用同一实际卷及目录。
                if (!v3Session.HasStableSourceView) sourcePath = physicalSource.PhysicalPath;
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
            SourceCaptureInput? captureInput = null;
            var forceCoreFull = invocationOptions.ForceFullCapture && !NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig);
            CaptureConcurrencyFence? captureFence = null;
            try
            {
                if (NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig))
                {
                    var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                    var workspace = (await history.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
                    var baseVersion = workspace?.GetSourceState(sourceId).BaseVersionId;
                    var tips = baseVersion is { } id ? await history.Query.GetMaterializationPolicyTipsAsync(id, cancellationToken).ConfigureAwait(false) : [];
                    captureFence = new(workspace?.StateRevision ?? -1, baseVersion, tips.Select(t => t.UpdateId).ToImmutableArray());
                }
                captureBaseline = forceCoreFull ? null : await NativeHistoryCoreGateway.LoadCaptureBaselineInsideOperationAsync(
                    operationRuntime,
                    operationLease,
                    sourceId,
                    cancellationToken).ConfigureAwait(false);
                if (captureBaseline?.ArtifactRootId is not null && NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig)
                    && !await NativeArtifactHostService.IsIncrementalBaselineAvailableAsync(runtimeConfig, captureBaseline, cancellationToken).ConfigureAwait(false))
                {
                    var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                    await history.CaptureBaselines.RemoveAsync(sourceId, captureBaseline.Revision, cancellationToken).ConfigureAwait(false);
                    captureBaseline = null;
                }
                if (captureBaseline is null && NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig))
                    captureBaseline = await NativeArtifactHostService.RebuildIncrementalBaselineAsync(runtimeConfig, sourceId.Value, cancellationToken).ConfigureAwait(false);
                if (captureBaseline is null && !forceCoreFull && !NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig))
                {
                    var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                    var archive = new SevenZipHistoryArchiveBackend(config);
                    var engine = new FolderRewind.History.Representation.RepresentationRuntime(
                    [new FolderRewind.History.Representation.CoreArchiveRepresentationHandler(archive),
                     new FolderRewind.History.Representation.SmartDeltaRepresentationHandler(archive)]);
                    captureBaseline = await HistoryCaptureBaselineRepair.RebuildAsync(history, engine, sourceId, cancellationToken).ConfigureAwait(false);
                }
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
                var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                await history.CaptureBaselines.RemoveAsync(sourceId, captureBaseline.Revision, cancellationToken).ConfigureAwait(false);
                captureBaseline = null;
            }
            var captureScope = CaptureScopePolicy.Determine(effectiveBoundary, effectiveBoundary);
            try
            {
                await v3Session.AcquireConsistencyAsync(cancellationToken).ConfigureAwait(false);
                await v3Session.PrepareMetadataRequirementsAsync(cancellationToken).ConfigureAwait(false);
                captureInput = ReadCaptureInput(sourcePath, runtimeConfig, runtimeFolder, sourceId, effectiveBoundary, captureBaseline, cancellationToken);
                captureInput.MetadataPaths = v3Session.MetadataPaths;
                var incremental = NativeArtifactHostService.IsIncrementalPolicy(runtimeConfig);
                if (captureBaseline is not null && ((incremental && captureBaseline.ArtifactRootId is null)
                    || (!incremental && captureBaseline.BaseRepresentationKind == RepresentationKind.PluginArtifact)))
                {
                    var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                    await history.CaptureBaselines.RemoveAsync(sourceId, captureBaseline.Revision, cancellationToken).ConfigureAwait(false);
                    captureBaseline = null;
                }
                if (incremental && captureBaseline is not null
                    && captureBaseline.SourcePathFingerprint != NativeArtifactHostService.SourcePathFingerprint(folder.Path))
                    captureBaseline = captureBaseline with { RequiresContentValidation = true };
                if (incremental && captureBaseline?.RequiresContentValidation == true)
                {
                    SourceDirectorySnapshot.Validate(captureInput.Directories, captureInput.Files.Keys);
                    if (captureInput.Files.Values.Sum(f => f.Size) > Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumBytes)
                        throw new CaptureInputException(sourcePath, new InvalidDataException("Native input exceeds its byte budget."));
                    Log($"[CaptureInput] phase=baseline-validation; mode={runtimeConfig.Archive.SourceReadMode}; source={sourcePath}", LogLevel.Info);
                    var validationInput = await captureInput.PrepareStatisticsValidationAsync(runtimeConfig.Archive.SourceReadMode,
                        CaptureTemporaryRoot, cancellationToken).ConfigureAwait(false);
                    LogCaptureInput(validationInput, validationInput.IsCopy ? "copy" : "protected_direct", runtimeConfig.Id);
                    captureBaseline = await NativeArtifactHostService.RevalidateStatisticsAsync(runtimeConfig, validationInput, captureBaseline,
                        captureInput.Files, cancellationToken).ConfigureAwait(false);
                }
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Processing");
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupRunning");
                });

                var directSemantic = incremental && config.Archive.Mode != BackupMode.Rolling;
                captureInput.Baseline = captureBaseline;
                if (invocationOptions.RequestSemanticCheckpoint && !directSemantic)
                    throw new InvalidOperationException(I18n.GetString("SemanticBackup_CheckpointStableRequired"));
                if (directSemantic)
                {
                    try
                    {
#if NATIVE_ARTIFACT_ACCEPTANCE
                        if (invocationOptions.RequestSemanticCheckpoint) await Development.NativeArtifactAcceptance.PauseUiFixtureAsync("checkpoint", cancellationToken);
#endif
                        var states = captureInput.Files;
                        var changes = CompareFileStates(states, captureBaseline?.FileStates);
                        captureInput.Changes = changes.ToCaptureDiff();
                        var directories = captureInput.Directories;
                        var directoriesUnchanged = captureBaseline is not null && directories.SequenceEqual(captureBaseline.Directories);
                        if (captureScope != CaptureScope.FullSource) throw new InvalidOperationException("Semantic capture requires the complete effective boundary.");
                        if (BackupSourceAvailabilityPolicy.IsUnavailable(runtimeFolder.SourceScope, states.Count))
                            captureResult = SourceCaptureResult.Unavailable(sourceId, captureScope);
                        else if (captureBaseline is not null && !changes.HasChanges && directoriesUnchanged
                            && (config.Archive.Mode == BackupMode.Smart || config.Archive.SkipIfUnchanged)
                            && !invocationOptions.RequestSemanticCheckpoint)
                            captureResult = SourceCaptureResult.NoChanges(sourceId, captureScope);
                        else
                        {
                            var fileStates = states.ToImmutableSortedDictionary(StringComparer.Ordinal);
                            var plan = FileChangeCapturePlan.Create(fileStates, captureBaseline, effectiveBoundary,
                                captureBaseline is not null && config.Archive.Mode == BackupMode.Smart,
                                invocationOptions.RequestSemanticCheckpoint, captureInput.Changes) with { Directories = directories };
                            var witness = new SourceCaptureBaselineCandidate(captureBaseline?.Revision ?? -1, string.Empty, 0,
                                fileStates, effectiveBoundary.Fingerprint)
                            { SourcePathFingerprint = NativeArtifactHostService.SourcePathFingerprint(folder.Path), Directories = directories };
                            SourceDirectorySnapshot.Validate(directories, fileStates.Keys);
                            if (fileStates.Values.Sum(f => f.Size) > Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumBytes)
                                throw new CaptureInputException(sourcePath, new InvalidDataException("Native input exceeds its byte budget."));
                            var view = await captureInput.PrepareForModeAsync(plan.Input.AddedFiles.Concat(plan.Input.ModifiedFiles),
                                runtimeConfig.Archive.SourceReadMode, protect: true, CaptureTemporaryRoot, cancellationToken).ConfigureAwait(false);
                            LogCaptureInput(view, view.IsCopy ? "copy" : "protected_direct", runtimeConfig.Id);
                            captureResult = await NativeArtifactHostService.CaptureDirectAsync(runtimeConfig,
                                v3Session.SourceResolution, plan, witness, effectiveBoundary, view.SourcePath, cancellationToken, v3Session.ArtifactOperation).ConfigureAwait(false);
                        }
                    }
                    catch (Plugin.Runtime.Artifacts.ArtifactCaptureTransformException transformError) when (!invocationOptions.RequestSemanticCheckpoint
                        && runtimeConfig.ArtifactTransformPolicy?.FailureBehavior == PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings)
                    {
                        // A sparse Native view is never a Full source. Start one independent capture.
                        captureInput.Dispose(); captureInput = null;
                        await v3Session.CompleteCaptureAsync().ConfigureAwait(false);
                        await v3Session.AcquireConsistencyAsync(cancellationToken).ConfigureAwait(false);
                        await v3Session.PrepareMetadataRequirementsAsync(cancellationToken).ConfigureAwait(false);
                        captureInput = ReadCaptureInput(sourcePath, runtimeConfig, runtimeFolder, sourceId, effectiveBoundary, null, cancellationToken);
                        captureInput.MetadataPaths = v3Session.MetadataPaths;
                        HistoryOperationDiagnostics.Current?.Count("native-independent-full-fallbacks");
                        Log($"[CaptureInput] phase=independent-full-fallback; reason={transformError.Message}", LogLevel.Warning);
                        captureResult = await DoFullBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, null,
                            folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task,
                            captureInput: captureInput, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (captureResult.BaselineCandidate is { } fallbackStats)
                            captureResult = captureResult.WithBaselineCandidate(fallbackStats with { ExpectedRevision = captureBaseline?.Revision ?? -1 });
                        captureResult = captureResult.WithVersionMetadata([], [new HistoryDiagnostic("plugin.artifact_primary_kept",
                            HistoryDiagnosticSeverity.Warning, transformError.Message)]);
                    }
                    success = captureResult.Success;
                    sourceUnavailable = captureResult.IsUnavailable;
                    generatedFileName = captureResult.FileName;
                }
                else if (incremental)
                {
                    throw new InvalidOperationException("Native capture does not support Rolling mode.");
                }
                else if (forceCoreFull)
                {
                    var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                    var cacheRevision = (await history.CaptureBaselines.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false))?.Revision ?? -1;
                    captureResult = await DoFullBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, null,
                        folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task,
                        originalSourcePath: physicalBinding?.ComparisonRoot, captureInput: captureInput, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (captureResult.BaselineCandidate is { } fullStats)
                        captureResult = captureResult.WithBaselineCandidate(fullStats with { ExpectedRevision = cacheRevision });
                    success = captureResult.Success;
                    sourceUnavailable = captureResult.IsUnavailable;
                    generatedFileName = captureResult.FileName;
                    HistoryOperationDiagnostics.Current?.Count("safety-full-captures");
                }
                else switch (config.Archive.Mode)
                {
                    case BackupMode.Smart:
                        {
                            var res = await DoSmartBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, captureBaseline, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task, captureInput: captureInput, cancellationToken: cancellationToken);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Rolling:
                        {
                            var res = await DoRollingBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, captureBaseline, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task, captureInput: captureInput, cancellationToken: cancellationToken);
                            captureResult = res;
                            success = res.Success;
                            generatedFileName = res.FileName;
                            sourceUnavailable = res.IsUnavailable;
                            break;
                        }
                    case BackupMode.Full:
                    default:
                        {
                            var res = await DoFullBackupAsync(sourceId, captureScope, sourcePath, backupSubDir, captureBaseline, folder.DisplayName, runtimeConfig, runtimeFolder.SourceScope, comment, task, captureInput: captureInput, cancellationToken: cancellationToken);
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
                    captureResult = await AddCaptureMetadataAsync(captureResult, captureInput, v3Session, operationRuntime, cancellationToken).ConfigureAwait(false);
                    if (captureResult.BaselineCandidate is { } captureStats)
                        captureResult = captureResult.WithBaselineCandidate(captureStats with
                        { SourcePathFingerprint = NativeArtifactHostService.SourcePathFingerprint(folder.Path) });
                }
                else if (incremental && captureResult?.Outcome == SourceCaptureOutcome.NoChanges && captureBaseline is not null)
                {
                    var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                    var representation = await history.Query.GetRepresentationAsync(captureBaseline.BaseRepresentationId, cancellationToken).ConfigureAwait(false);
                    if (representation is not null)
                        await history.CaptureBaselines.SaveAsync(sourceId,
                            new(captureBaseline.Revision, captureBaseline.PayloadPath, captureBaseline.ConsecutiveSmartCaptures,
                                captureBaseline.FileStates, captureBaseline.BoundaryFingerprint)
                            { ArtifactRootId = captureBaseline.ArtifactRootId, EvidenceIdentity = captureBaseline.EvidenceIdentity, Directories = captureBaseline.Directories,
                                ContentRealizationId = (await history.WorkspaceStore.LoadAsync(cancellationToken)).Value!.GetSourceState(sourceId).ContentRealizationId,
                                SourcePathFingerprint = NativeArtifactHostService.SourcePathFingerprint(folder.Path) },
                            captureBaseline.BaseVersionId, representation, cancellationToken).ConfigureAwait(false);
                }
                if (captureResult is not null && captureFence is not null)
                    captureResult = captureResult with { ConcurrencyFence = captureFence };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                success = false;
            }
            catch (Exception ex)
            {
                var message = ex is CaptureInputException inputError
                    ? I18n.Format("BackupSourceView_InputFailed", inputError.SourcePath, inputError.InnerException?.GetBaseException().Message ?? inputError.Message)
                    : ex.Message;
                Log(I18n.Format("BackupService_Log_Exception", message), LogLevel.Error);
                if (ex is CaptureInputException failedInput)
                    Log($"[CaptureInput] phase={failedInput.Phase}; path={failedInput.SourcePath}; nativeError={failedInput.NativeErrorCode}; cause={failedInput.InnerException}", LogLevel.Error);
                success = false;
                await RunOnUIAsync(() => { if (string.IsNullOrEmpty(task.ErrorMessage)) task.ErrorMessage = message; });
            }

            var operationDiagnostics = v3Session.Diagnostics.ToList();
            try { captureInput?.Dispose(); }
            catch (Exception cleanupError)
            {
                Log($"[CaptureInput] phase=cleanup; {cleanupError}", LogLevel.Warning);
                operationDiagnostics.Add(new("backup.source_input_cleanup_failed", DiagnosticSeverity.Warning, "Capture", "folderrewind.core",
                    new Dictionary<string, string> { ["message"] = cleanupError.Message, ["phase"] = "cleanup" }));
            }
            var completionOutcome = operationDiagnostics.Any(value => value.Severity == DiagnosticSeverity.Warning)
                || captureResult?.Diagnostics.Any(value => value.Severity == HistoryDiagnosticSeverity.Warning) == true
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
                    warningDiagnostics: operationDiagnostics,
                    task: task);
            }
            if (!success)
            {
                if (captureResult?.Outcome == SourceCaptureOutcome.Captured)
                {
                    if (captureResult.CommitParticipant is { } participant)
                        try { await participant.AbortCommitAsync(CancellationToken.None).ConfigureAwait(false); }
                        finally { participant.EndOperation(); }
                    if (captureResult.CleanupHandle is { } cleanup)
                        await cleanup.CleanupAsync(CancellationToken.None).ConfigureAwait(false);
                    captureResult = null;
                }
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: task.ErrorMessage,
                    captureResult: captureResult,
                    operationOutcome: canceled ? OperationOutcome.Canceled : OperationOutcome.Failed,
                    warningDiagnostics: operationDiagnostics,
                    task: task);
            }
            if (!string.IsNullOrWhiteSpace(generatedFileName))
            {
                return CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.NewArchive,
                    captureResult: captureResult,
                    operationOutcome: completionOutcome,
                    warningDiagnostics: operationDiagnostics,
                    generatedFileName: generatedFileName,
                    task: task);
            }

            return CreateSourceOutcome(
                folder,
                BackupSourceExecutionStatus.Reused,
                captureResult: captureResult,
                operationOutcome: completionOutcome == OperationOutcome.SuccessWithWarnings ? completionOutcome : null,
                warningDiagnostics: operationDiagnostics,
                task: task);
        }

        private static BackupSourceExecutionOutcome CreateSourceOutcome(
            ManagedFolder folder,
            BackupSourceExecutionStatus status,
            string? errorMessage = null,
            SourceCaptureResult? captureResult = null,
            OperationOutcome? operationOutcome = null,
            string? generatedFileName = null,
            BackupTask? task = null,
            IEnumerable<PluginDiagnostic>? warningDiagnostics = null) => new()
        {
            Status = status,
            FolderId = Guid.TryParse(folder.Id, out var folderId) ? folderId : null,
            FolderPath = folder.Path ?? string.Empty,
            FolderName = folder.DisplayName ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            CaptureResult = captureResult,
            WarningDiagnostics = OperationDiagnosticFormatter.CaptureWarnings(warningDiagnostics ?? [], captureResult?.Diagnostics ?? []),
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
            foreach (var participant in outcomes.Select(o => o.CaptureResult?.CommitParticipant).OfType<ICaptureCommitParticipant>())
            {
                try { await participant.AbortCommitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
                finally { participant.EndOperation(); }
            }
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
            IEnumerable<string>? deletedFiles = null,
            string? captureVerificationLevel = null)
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
                deletedFiles,
                captureVerificationLevel: captureVerificationLevel);

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





    }
}
