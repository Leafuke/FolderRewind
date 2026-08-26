using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Operations;
using FolderRewind.Services.Plugins.V3;
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
        // 还原入口、还原链和安全还原工作区集中在这里，便于后续单独审计恢复路径。

        /// <summary>
        /// 恢复模式：Clean=清空目标后还原（最安全）；Overwrite=直接覆盖，保留未被覆盖的文件。
        /// 部分备份（IsPartialBackup）无法承载 Clean 语义，会被强制提升为 Overwrite。
        /// </summary>
        public enum RestoreMode
        {
            Clean = 0,      // 清空目标后还原 (最安全)
            Overwrite = 1   // 直接覆盖 (保留未被覆盖的文件)
        }

        /// <summary>
        /// 解析实际生效的恢复模式：部分备份强制 Overwrite，其余按调用方请求返回。
        /// </summary>
        public static RestoreMode ResolveEffectiveRestoreMode(HistoryItem? historyItem, RestoreMode requestedMode)
            => RestoreModePolicy.UseOverwrite(
                historyItem?.IsPartialBackup == true,
                requestedMode == RestoreMode.Clean)
                ? RestoreMode.Overwrite
                : RestoreMode.Clean;

        /// <summary>
        /// 恢复公共入口：按配置归属分发。宿主自有配置（folderrewind.core）直接进入核心还原；
        /// 插件拥有的配置先做操作解析与预检，再经插件的 Restore Coordinator 包裹
        /// （供其执行外部存档退出等前置动作），实际还原由续延门（continuation gate）
        /// 保证只执行一次——优先语义产物恢复，插件不可用时回退到核心归档还原。
        /// </summary>
        public static async Task<bool> RestoreBackupAsync(BackupConfig config, ManagedFolder folder, HistoryItem historyItem, RestoreMode mode)
        {
            var owner = new PluginId(config.Kind?.OwnerId ?? "folderrewind.core");
            if (string.Equals(owner.Value, "folderrewind.core", StringComparison.Ordinal))
            {
                return await RestoreBackupCoreAsync(config, folder, historyItem, mode);
            }

            var runtime = PluginV3RuntimeService.Runtime;
            var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
            var folderId = Guid.Parse(folder.Id);
            var folderSnapshot = configSnapshot.Folders.Single(value => value.FolderId == folderId);
            var declaration = PluginV3RuntimeService.FindKind(configSnapshot.Kind);
            using var coordinatorLease = runtime.TryAcquire<IRestoreCoordinatorCapability>(
                owner,
                capability => capability.Kind == configSnapshot.Kind);
            var resolution = PluginOperationResolver.Resolve(new PluginOperationResolutionRequest(
                declaration ?? new ConfigKindDeclaration(
                    configSnapshot.Kind,
                    new LocalizedText(config.Name, new Dictionary<string, string>()),
                    new LocalizedText(string.Empty, new Dictionary<string, string>()),
                    string.Empty,
                    BackupFallbackPolicy.RawWithWarnings,
                    RestoreCoordinationPolicy.Required),
                PluginOperationKind.Restore,
                runtime.GetSnapshot(owner).State,
                false,
                false,
                ConsistencyIntent.Prefer,
                false,
                coordinatorLease is not null));
            if (resolution.Readiness == OperationReadiness.Blocked
                || coordinatorLease is null)
            {
                var message = "The restore owner is missing, disabled, failed, or lacks its required Restore Coordinator.";
                Log($"[PluginV3] {message} Owner={owner}", LogLevel.Error);
                NotificationService.ShowError(message);
                return false;
            }

            if (!await PreflightV3RestoreAsync(config, folder, historyItem))
            {
                const string message = "Plugin v3 restore preflight failed before external Save & Exit.";
                Log($"[PluginV3] {message}", LogLevel.Error);
                NotificationService.ShowError(message);
                return false;
            }

            var continuation = new RestoreMutationContinuationGate(async cancellationToken =>
            {
                var semantic = await TryRestoreSemanticArtifactAsync(
                    config,
                    folder,
                    historyItem,
                    mode,
                    cancellationToken);
                if (semantic.HasValue)
                {
                    return semantic.Value ? OperationOutcome.Success : OperationOutcome.Failed;
                }
                return await RestoreBackupCoreAsync(
                    config,
                    folder,
                    historyItem,
                    mode)
                    ? OperationOutcome.Success
                    : OperationOutcome.Failed;
            });
            try
            {
                var result = await coordinatorLease.Capability.CoordinateAsync(
                    new RestoreCoordinatorRequest(
                        configSnapshot,
                        folderSnapshot,
                        historyItem.Id,
                        continuation.InvokeAsync),
                    coordinatorLease.Context);
                return continuation.WasInvoked
                    && result.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings;
            }
            catch (Exception ex)
            {
                Log($"[PluginV3] Restore coordination failed: {ex.Message}", LogLevel.Error);
                NotificationService.ShowError(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 插件恢复预检（在插件执行外部"保存并退出"之前跑完）：
        /// 产物图路径核对产物闭包可用性、账本校验与图修订一致性；
        /// 核心归档路径核对恢复链完整（可选云端补链）、7z 可用，并按设置预校验归档完整性。
        /// 任一环节失败都返回 false，协调器不再启动。
        /// </summary>
        private static async Task<bool> PreflightV3RestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem historyItem)
        {
            try
            {
                if (historyItem.ArtifactRootId.HasValue)
                {
                    var closure = await CloudSyncService.EnsureArtifactClosureAvailableAsync(
                        config,
                        folder,
                        historyItem).ConfigureAwait(false);
                    if (!closure.Success)
                    {
                        Log("[PluginV3] " + closure.Message, LogLevel.Error);
                        return false;
                    }
                    var store = new FileArtifactLedgerStore(config.DestinationPath);
                    var ledger = await store.LoadAsync().ConfigureAwait(false);
                    var historyRoot = ledger.HistoryRoots.Single(value =>
                        StringComparer.Ordinal.Equals(value.HistoryItemId, historyItem.Id)
                        && value.RootArtifactId.Value == historyItem.ArtifactRootId.Value);
                    if (!StringComparer.Ordinal.Equals(ledger.Revision.Value, historyItem.ArtifactGraphRevision))
                        throw new InvalidDataException("History and Artifact graph revisions do not match.");
                    var reachable = ArtifactLedgerValidator.ComputeReachable(ledger, [historyItem.Id]);
                    await store.VerifyArtifactsAsync(ledger, reachable).ConfigureAwait(false);
                    var root = ledger.Artifacts.Single(value => value.ArtifactId == historyRoot.RootArtifactId);
                    if (!StringComparer.Ordinal.Equals(root.RestoreStrategyId.PluginId.Value, "folderrewind.core"))
                    {
                        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IRestoreMaterializerCapability>(
                            root.RestoreStrategyId.PluginId,
                            capability => capability.RestoreStrategyId == root.RestoreStrategyId);
                        if (lease is null)
                            throw new InvalidOperationException("Artifact Restore Strategy owner is unavailable.");
                        return true;
                    }
                }

                var path = HistoryService.GetBackupFilePath(config, folder, historyItem);
                var incremental = BackupArchiveTypePolicy.IsIncremental(historyItem.BackupType)
                    || BackupArchiveTypePolicy.InferFromFileName(historyItem.FileName)
                        .Equals("Smart", StringComparison.OrdinalIgnoreCase);
                if (ConfigService.CurrentConfig?.GlobalSettings?.AutoDownloadMissingCloudBackupsBeforeRestore == true
                    && CloudSyncService.CanUseManualCloudActions(config)
                    && (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || incremental))
                {
                    await CloudSyncService.EnsureRestoreChainAvailableAsync(config, folder, historyItem)
                        .ConfigureAwait(false);
                    path = HistoryService.GetBackupFilePath(config, folder, historyItem);
                }
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                var sevenZip = ResolveSevenZipExecutable();
                if (string.IsNullOrWhiteSpace(sevenZip)) return false;
                var chain = BuildRestoreChainWithStatus(
                    new DirectoryInfo(Path.GetDirectoryName(path)!),
                    new FileInfo(path),
                    historyItem.BackupType,
                    config,
                    string.IsNullOrWhiteSpace(historyItem.FolderName) ? folder.DisplayName : historyItem.FolderName);
                if (chain.Status != RestoreChainBuildStatus.Success || chain.Chain.Count == 0) return false;
                if (!TryResolveRequiredPassword(config, out var password)) return false;
                return config.Archive?.VerifyArchiveBeforeRestore != true
                    || await ValidateRestoreChainAsync(chain.Chain, sevenZip, password, restoreTask: null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("[PluginV3] Restore preflight error: " + ex.Message, LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// 核心还原管线：生效模式解析 → 恢复链构建（增量缺基全量时经用户确认回退兼容链路）
        /// → 可选的恢复前安全备份（BackupBeforeRestore）→ 元数据完整时构建精确 Smart Clean 计划
        /// → 7z t 全链完整性校验 → 安全恢复工作区事务（准备/提交/回滚）或原地清理
        /// → 执行解压（精确分组或整链）→ 白名单合并与收尾通知。
        /// </summary>
        /// <remarks>
        /// Clean + SafeRestore 组合下目标目录先整体挪到同级临时快照，在干净目录中还原，
        /// 成功才提交（回迁白名单内容后删除快照），失败则优先整体回滚；
        /// 未启用安全快照时退化为"原地清理（保留白名单）+ 覆盖还原"，无回滚保障。
        /// 兼容链路（缺基全量）无法精确执行 Clean 语义，一律强制为覆盖式还原。
        /// </remarks>
        private static async Task<bool> RestoreBackupCoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem historyItem,
            RestoreMode mode)
        {
            RestoreMode requestedMode = mode;
            RestoreMode effectiveMode = ResolveEffectiveRestoreMode(historyItem, requestedMode);
            if (effectiveMode != requestedMode)
            {
                Log(
                    $"[Restore] Partial backup '{historyItem.FileName}' requested Clean restore; forcing Overwrite.",
                    LogLevel.Warning);
            }

            int configIndex = GetConfigIndex(config);
            string? backupFilePath = HistoryService.GetBackupFilePath(config, folder, historyItem);
            string resolvedFolderName = string.IsNullOrWhiteSpace(historyItem.FolderName)
                ? folder.DisplayName
                : historyItem.FolderName;
            string targetDir = folder.Path;
            var archiveSettings = config.Archive;
            bool safeRestoreEnabled = archiveSettings?.SafeRestoreEnabled ?? true;
            bool verifyArchiveBeforeRestore = archiveSettings?.VerifyArchiveBeforeRestore ?? true;
            // 历史数据可能是旧格式，这里同时看 BackupType 和文件名前缀做兼容判断。
            bool targetIsIncremental = BackupArchiveTypePolicy.IsIncremental(historyItem.BackupType)
                || BackupArchiveTypePolicy.InferFromFileName(historyItem.FileName).Equals("Smart", StringComparison.OrdinalIgnoreCase);

            string? safeRestoreTempDir = null;
            bool safeRestoreWorkspacePrepared = false;
            bool useCompatibilityReverseRestore = false;
            bool restoreFailed = false;
            bool restoreStarted = false;
            bool effectiveCleanRestore = effectiveMode == RestoreMode.Clean;
            PathRuleMatcher? restoreWhitelistMatcher = null;
            SmartRestorePlan? smartRestorePlan = null;
            List<FileInfo> restoreChain = new();

            var restoreTask = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = I18n.Format("BackupService_Task_Restoring"),
                IconGlyph = "\uE777",
                Progress = 0,
                IsIndeterminate = true
            };
            await RunOnUIAsync(() => ActiveTasks.Insert(0, restoreTask));

            async Task FailAsync(string message, string reason)
            {
                await RunOnUIAsync(() =>
                {
                    restoreTask.Status = I18n.Format("BackupService_Task_RestoreFailed");
                    restoreTask.IsCompleted = true;
                    restoreTask.IsIndeterminate = false;
                    restoreTask.IsSuccess = false;
                    restoreTask.ErrorMessage = message;
                });

                if (restoreStarted)
                {
                    BroadcastRestoreLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = reason });
                    BroadcastRestoreEvent(configIndex, config, folder, "restore_finished", new Dictionary<string, string?>
                    {
                        ["status"] = "failure",
                        ["reason"] = reason
                    });
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, message);
            }

            if (effectiveCleanRestore && config.Filters?.RestoreWhitelist?.Count > 0)
            {
                try
                {
                    restoreWhitelistMatcher = PathRuleMatcher.CreateForRestore(
                        config.Filters.RestoreWhitelist,
                        targetDir);
                }
                catch (PathRuleValidationException ex)
                {
                    Log($"[Filter] Restore whitelist validation failed: {ex.Message}", LogLevel.Error);
                    await FailAsync(ex.Message, "invalid_restore_filter");
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(backupFilePath))
            {
                string message = "Invalid backup path in history record.";
                Log(message, LogLevel.Error);
                await FailAsync(message, "invalid_backup_path");
                return false;
            }

            string resolvedBackupFilePath = backupFilePath;

            // 增量链要求所有成员都在本地：目标文件缺席或目标是增量时，先尝试云端补齐整条链。
            bool shouldAttemptCloudRestoreCompletion =
                ConfigService.CurrentConfig?.GlobalSettings?.AutoDownloadMissingCloudBackupsBeforeRestore == true
                && CloudSyncService.CanUseManualCloudActions(config)
                && (!File.Exists(resolvedBackupFilePath) || targetIsIncremental);

            if (shouldAttemptCloudRestoreCompletion)
            {
                var cloudRestoreResult = await CloudSyncService.EnsureRestoreChainAvailableAsync(config, folder, historyItem);
                if (!cloudRestoreResult.Success)
                {
                    Log($"[Restore] Cloud restore-chain completion skipped or failed: {cloudRestoreResult.Message}", LogLevel.Warning);
                }

                backupFilePath = HistoryService.GetBackupFilePath(config, folder, historyItem);
                if (!string.IsNullOrWhiteSpace(backupFilePath))
                {
                    resolvedBackupFilePath = backupFilePath;
                }
            }

            if (archiveSettings?.BackupBeforeRestore == true)
            {
                Log(I18n.Format("BackupService_Log_BackupBeforeRestore", folder.DisplayName), LogLevel.Info);
                try
                {
                    var beforeRestore = await BackupFolderCoreAsync(
                        config,
                        folder,
                        "BeforeRestore",
                        BackupInvocationOptions.ForInternal().WithComment("BeforeRestore"),
                        createdByRunId: null);
                    if (beforeRestore.Status is BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(beforeRestore.ErrorMessage)
                            ? "BackupBeforeRestore did not produce a safe restore point."
                            : beforeRestore.ErrorMessage);
                    }
                    if (beforeRestore.CreatedNewArchive)
                    {
                        await PruneRetainedSourceArchivesAsync(config);
                    }
                    Log(I18n.Format("BackupService_Log_BackupBeforeRestoreCompleted"), LogLevel.Info);
                }
                catch (Exception ex)
                {
                    var message = I18n.Format("BackupService_Log_BackupBeforeRestoreFailed", ex.Message);
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "backup_before_restore_failed");
                    return false;
                }
            }

            if (!File.Exists(resolvedBackupFilePath))
            {
                string message = I18n.Format("BackupService_Log_BackupFileNotFound", resolvedBackupFilePath);
                Log(message, LogLevel.Error);
                await FailAsync(message, "no_backup_found");
                return false;
            }

            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe))
            {
                string message = I18n.Format("BackupService_Log_SevenZipNotFound");
                await FailAsync(message, "seven_zip_not_found");
                return false;
            }

            var backupDir = new DirectoryInfo(Path.GetDirectoryName(resolvedBackupFilePath)!);
            var targetFile = new FileInfo(resolvedBackupFilePath);
            var chainResult = BuildRestoreChainWithStatus(backupDir, targetFile, historyItem.BackupType, config, resolvedFolderName);

            if (targetIsIncremental && chainResult.Status == RestoreChainBuildStatus.MissingBaseFull)
            {
                // 老用户历史里可能缺少基准 Full，这里给一次人工确认后回退到兼容链路。
                bool proceed = await ConfirmMissingBaseFullFallbackAsync(folder.DisplayName, historyItem.FileName);
                if (!proceed)
                {
                    await RunOnUIAsync(() =>
                    {
                        restoreTask.Status = I18n.GetString("Common_Canceled");
                        restoreTask.IsCompleted = true;
                        restoreTask.IsIndeterminate = false;
                        restoreTask.IsSuccess = false;
                        restoreTask.ErrorMessage = I18n.GetString("Common_Canceled");
                    });
                    return false;
                }

                restoreChain = BuildReverseCompatibilityChain(backupDir, targetFile, config, resolvedFolderName);
                useCompatibilityReverseRestore = true;
                // 兼容链路无法精确执行 Clean 语义，这里强制退化到覆盖式还原。
                effectiveCleanRestore = false;
                effectiveMode = RestoreMode.Overwrite;

                if (restoreChain.Count == 0)
                {
                    string message = I18n.Format("BackupService_Log_RestoreChainNotFound");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "reverse_chain_not_found");
                    return false;
                }
            }
            else
            {
                restoreChain = chainResult.Chain;
                if (restoreChain.Count == 0)
                {
                    string message = I18n.Format("BackupService_Log_RestoreChainNotFound");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "restore_chain_not_found");
                    return false;
                }
            }

            if (effectiveCleanRestore && targetIsIncremental && !useCompatibilityReverseRestore)
            {
                if (!TryResolveBackupStoragePaths(
                    config.DestinationPath,
                    resolvedFolderName,
                    folder.Path,
                    out _,
                    out _,
                    out var metadataDir))
                {
                    string message = I18n.GetString("BackupService_Log_InvalidRestoreStorageFolderName");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "invalid_folder_name");
                    return false;
                }

                var metadataLoadResult = await LoadBackupMetadataAsync(metadataDir, restoreChain.Select(file => file.Name)).ConfigureAwait(false);
                var metadata = ConvertToAggregateMetadata(metadataLoadResult);

                // 元数据完整时启用精确 Smart Clean，可避免“全链解压+覆盖”带来的额外写入。
                if (metadata != null
                    && !metadataLoadResult.RecordLoadFailed
                    && !metadataLoadResult.HasMissingRequestedRecords
                    && TryBuildSmartRestorePlan(restoreChain, metadata, out var plan))
                {
                    smartRestorePlan = plan;
                    Log($"[Restore] Exact Smart Clean restore enabled for {historyItem.FileName}", LogLevel.Info);
                }
                else
                {
                    Log($"[Restore] Exact Smart Clean restore unavailable for {historyItem.FileName}, falling back to compatibility chain extraction.", LogLevel.Warning);
                }
            }

            Log(I18n.Format("BackupService_Log_RestoreBegin", folder.DisplayName), LogLevel.Info);
            Log(I18n.Format("BackupService_Log_RestoreTargetBackup", historyItem.FileName), LogLevel.Info);
            Log(I18n.Format("BackupService_Log_RestoreTargetPath", targetDir), LogLevel.Info);


            if (!TryResolveRequiredPassword(config, out var restorePassword, restoreTask))
            {
                string message = string.IsNullOrWhiteSpace(restoreTask.ErrorMessage)
                    ? MissingEncryptionPasswordMessage
                    : restoreTask.ErrorMessage!;
                await FailAsync(message, "encryption_password_missing");
                return false;
            }
            // 智能还原方案与普通还原方案都共用这一段完整性校验入口。
            var archivesToVerify = smartRestorePlan?.Chain ?? restoreChain;

            if (verifyArchiveBeforeRestore)
            {
                Log(I18n.Format("BackupService_Log_RestoreIntegrityCheckBegin", archivesToVerify.Count), LogLevel.Info);
                bool verifyPassed = await ValidateRestoreChainAsync(archivesToVerify, sevenZipExe, restorePassword, restoreTask);
                if (!verifyPassed)
                {
                    string message = I18n.Format("BackupService_Log_RestoreIntegrityCheckFailedStop");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "archive_integrity_check_failed");
                    return false;
                }

                Log(I18n.Format("BackupService_Log_RestoreIntegrityCheckPassed"), LogLevel.Info);
            }

            var restoreModeFields = new Dictionary<string, string?>
            {
                ["requested_mode"] = requestedMode.ToString().ToLowerInvariant(),
                ["effective_mode"] = effectiveMode.ToString().ToLowerInvariant()
            };
            BroadcastRestoreLifecycle("command_started", restoreModeFields);
            BroadcastRestoreEvent(configIndex, config, folder, "restore_started", restoreModeFields);
            restoreStarted = true;

            if (effectiveCleanRestore && safeRestoreEnabled)
            {
                // 先把目标目录挪到临时快照，再在空目录还原；失败时可以整体回滚。
                if (!TryPrepareSafeRestoreWorkspace(targetDir, out safeRestoreTempDir, out var prepareError))
                {
                    string message = I18n.Format("BackupService_Log_RestoreSnapshotPrepareFailed", prepareError ?? "Unknown error");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "snapshot_prepare_failed");
                    return false;
                }

                safeRestoreWorkspacePrepared = !string.IsNullOrWhiteSpace(safeRestoreTempDir);
                if (safeRestoreWorkspacePrepared)
                {
                    Log(I18n.Format("BackupService_Log_RestoreSnapshotPrepared", safeRestoreTempDir ?? string.Empty), LogLevel.Info);
                }
            }
            else if (!Directory.Exists(targetDir))
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                }
                catch (Exception ex)
                {
                    string message = I18n.Format("BackupService_Log_RestoreCreateTargetDirFailed", ex.Message);
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "create_dir_failed");
                    return false;
                }
            }

            if (effectiveCleanRestore && !safeRestoreWorkspacePrepared)
            {
                // 未启用安全快照时，只能原地清理后再还原（会保留白名单路径）。
                Log(I18n.Format("BackupService_Log_RestoreCleaningTarget"), LogLevel.Info);
                var restoreWhitelist = config.Filters?.RestoreWhitelist;
                bool hasWhitelist = restoreWhitelist != null && restoreWhitelist.Count > 0;

                try
                {
                    DirectoryInfo di = new DirectoryInfo(targetDir);
                    foreach (var entry in di.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                    {
                        if (hasWhitelist && restoreWhitelistMatcher?.IsMatch(entry.FullName) == true)
                        {
                            Log(I18n.Format("BackupService_Log_RestoreWhitelistSkip", entry.Name), LogLevel.Info);
                            continue;
                        }

                        try
                        {
                            if (entry is DirectoryInfo dirEntry)
                            {
                                ClearReadonlyAttributes(dirEntry.FullName);
                                dirEntry.Delete(true);
                            }
                            else if (entry is FileInfo fileEntry)
                            {
                                if ((fileEntry.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                {
                                    fileEntry.Attributes &= ~FileAttributes.ReadOnly;
                                }
                                fileEntry.Delete();
                            }
                        }
                        catch (Exception entryEx)
                        {
                            Log(I18n.Format("BackupService_Log_RestoreDeleteFileFailed", entry.Name, entryEx.Message), LogLevel.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(I18n.Format("BackupService_Log_RestoreCleanFailedContinueOverwrite", ex.Message), LogLevel.Warning);
                }
            }

            bool restoreSucceeded;
            if (smartRestorePlan != null && effectiveCleanRestore)
            {
                restoreSucceeded = await ApplySmartRestorePlanAsync(smartRestorePlan, targetDir, sevenZipExe, restorePassword, restoreTask);
            }
            else
            {
                restoreSucceeded = await ApplyRestoreChainAsync(restoreChain, targetDir, sevenZipExe, restorePassword, restoreTask);
            }

            if (!restoreSucceeded)
            {
                restoreFailed = true;
                string message = string.IsNullOrWhiteSpace(restoreTask.ErrorMessage)
                    ? I18n.GetString("BackupService_Log_RestoreExtractFailed")
                    : restoreTask.ErrorMessage!;
                Log(message, LogLevel.Error);
            }
            else
            {
                CleanupInternalRestoreMarkers(targetDir);

                if (safeRestoreWorkspacePrepared && !string.IsNullOrWhiteSpace(safeRestoreTempDir))
                {
                    // 还原成功后再提交快照工作区，最后一步才真正删除旧目录。
                    if (!TryCommitSafeRestoreWorkspace(
                        targetDir,
                        safeRestoreTempDir,
                        restoreWhitelistMatcher,
                        out var commitError))
                    {
                        restoreFailed = true;
                        string message = I18n.Format("BackupService_Log_RestoreCommitFailed", commitError ?? "Unknown error");
                        Log(message, LogLevel.Error);
                        await RunOnUIAsync(() => restoreTask.ErrorMessage = message);
                    }
                }
            }

            if (restoreFailed)
            {
                if (safeRestoreWorkspacePrepared && !string.IsNullOrWhiteSpace(safeRestoreTempDir))
                {
                    // 只要失败就优先尝试整体回滚，尽量回到还原前状态。
                    Log(I18n.Format("BackupService_Log_RestoreRollbackBegin", safeRestoreTempDir), LogLevel.Warning);
                    if (TryRollbackSafeRestoreWorkspace(targetDir, safeRestoreTempDir, out var rollbackError))
                    {
                        Log(I18n.Format("BackupService_Log_RestoreRollbackSuccess"), LogLevel.Info);
                    }
                    else
                    {
                        string message = I18n.Format("BackupService_Log_RestoreRollbackFailed", rollbackError ?? "Unknown error");
                        Log(message, LogLevel.Error);
                        await RunOnUIAsync(() =>
                        {
                            if (string.IsNullOrWhiteSpace(restoreTask.ErrorMessage))
                            {
                                restoreTask.ErrorMessage = message;
                            }
                        });
                    }
                }

                string failureMessage = string.IsNullOrWhiteSpace(restoreTask.ErrorMessage)
                    ? I18n.GetString("BackupService_Log_RestoreExtractFailed")
                    : restoreTask.ErrorMessage!;
                await FailAsync(failureMessage, "command_failed");
                return false;
            }

            await RunOnUIAsync(() =>
            {
                restoreTask.Status = I18n.Format("BackupService_Task_RestoreCompleted");
                restoreTask.Progress = 100;
                restoreTask.IsCompleted = true;
                restoreTask.IsIndeterminate = false;
                restoreTask.IsSuccess = true;
            });

            Log(I18n.Format("BackupService_Log_RestoreCompleted"), LogLevel.Info);
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

        /// <summary>
        /// 通过备份文件名还原（供 KnotLink 远程调用使用），默认覆盖模式。
        /// </summary>
        public static async Task RestoreBackupAsync(BackupConfig config, ManagedFolder folder, string backupFileName)
        {
            await RestoreBackupAsync(config, folder, backupFileName, RestoreMode.Overwrite);
        }

        public static async Task RestoreBackupAsync(BackupConfig config, ManagedFolder folder, string backupFileName, RestoreMode mode)
        {
            // 构造一个临时的 HistoryItem
            string backupType = HistoryService.GetBackupTypeForFile(config.Id, folder.DisplayName, backupFileName)
                ?? BackupArchiveTypePolicy.InferFromFileName(backupFileName);
            var existingEntry = HistoryService.TryGetEntry(config.Id, folder.Path, backupFileName);

            var historyItem = new HistoryItem
            {
                Id = existingEntry?.Id ?? string.Empty,
                ConfigId = config.Id,
                FolderId = existingEntry?.FolderId,
                FolderPath = folder.Path,
                FolderName = folder.DisplayName,
                FileName = backupFileName,
                BackupType = backupType,
                IsPartialBackup = existingEntry?.IsPartialBackup ?? false,
                ArtifactRootId = existingEntry?.ArtifactRootId,
                ArtifactGraphRevision = existingEntry?.ArtifactGraphRevision ?? string.Empty
            };

            await RestoreBackupAsync(config, folder, historyItem, mode);
        }
    }
}
