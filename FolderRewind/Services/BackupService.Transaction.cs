using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services;

public static partial class BackupService
{
    internal sealed class BackupTransactionExecutionResult
    {
        public OperationOutcome Outcome { get; init; } = OperationOutcome.Failed;
        public bool CreatedNewArchive { get; init; }
        public IReadOnlyList<BackupSourceExecutionOutcome> SourceOutcomes { get; init; } = Array.Empty<BackupSourceExecutionOutcome>();
        public HistoryCommitBatch? CommittedBatch { get; init; }
        public SafetySnapshot? SafetySnapshot => CommittedBatch?.NewSafetySnapshot;
        public bool HistoryRecoveryRequired { get; init; }
        public string? RecoveryMessage { get; init; }

        public static BackupTransactionExecutionResult Blocked(string message) => new()
        {
            Outcome = OperationOutcome.Blocked,
            CreatedNewArchive = false,
            SourceOutcomes = Array.Empty<BackupSourceExecutionOutcome>()
        };

        public static BackupTransactionExecutionResult Canceled(IReadOnlyList<BackupSourceExecutionOutcome> outcomes) => new()
        {
            Outcome = OperationOutcome.Canceled,
            CreatedNewArchive = false,
            SourceOutcomes = outcomes
        };

        public static BackupTransactionExecutionResult Failed(IReadOnlyList<BackupSourceExecutionOutcome> outcomes, string message) => new()
        {
            Outcome = OperationOutcome.Failed,
            CreatedNewArchive = false,
            SourceOutcomes = outcomes
        };
    }

    /// <summary>
    /// 统一编排配置级备份事务生命周期：
    /// 1. 获取配置互斥操作门（Operation Gate）与历史就绪校验；
    /// 2. 为配置下的全部 Source 统一解析并冻结 V3 有效策略与边界（Effective Source Boundary）；
    /// 3. 执行 History boundary preflight 检查，发现未参与本次捕获但边界发生漂移的 Source 时于 Capture 前提前阻断；
    /// 4. 仅为请求的 Source 启动捕获（Capture），在此阶段获取 consistency lease 并生成/校验归档产物；
    /// 5. 组装权威 HistoryConfigSnapshot 并执行 Native History 原子提交；
    /// 6. 以 History Commit 为 durable boundary，仅对边界前的失败执行回滚清理；
    /// 7. 在 durable boundary 后执行终态处理、云同步排队与保留期清理，辅助失败只降级为 warning；
    /// 8. 保持 History 已确定的 Failed/Partial 聚合结果不被 post-commit warning 反向改写。
    /// </summary>
    internal static async Task<BackupTransactionExecutionResult> ExecuteBackupTransactionAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> requestedFolders,
        BackupInvocationOptions invocationOptions,
        HistoryCommitIntent intent = HistoryCommitIntent.AdvanceBranch,
        HistorySafetySnapshotIntent? safetySnapshotIntent = null,
        CancellationToken cancellationToken = default)
    {
        if (NativeHostMutationContext.IsNestedMutationBlocked)
        {
            return BackupTransactionExecutionResult.Blocked("Nested mutation is blocked.");
        }

        if (config is null || requestedFolders is null || requestedFolders.Count == 0)
        {
            return BackupTransactionExecutionResult.Blocked("Invalid backup configuration or empty requested folders.");
        }

        invocationOptions ??= BackupInvocationOptions.Default;

        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(config.Id, cancellationToken).ConfigureAwait(false);
        _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);

        var startedAtUtc = DateTimeOffset.UtcNow;
        int configIndex = GetConfigIndex(config);

        // 1. 为全部 configured Sources 解析并冻结统一的 V3 有效策略与边界
        // 关键设计：即使只备份部分 Source（如子集备份），也必须知道全部 configured Sources 在本次事务中的权威边界。
        var resolutions = new Dictionary<string, PluginV3BackupSourceResolution>(StringComparer.Ordinal);
        foreach (var folder in config.SourceFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = await PluginV3BackupSourceResolver.ResolveAsync(config, folder, cancellationToken).ConfigureAwait(false);
            resolutions[folder.Id] = resolution;

            // 如果任何配置下的 Source 无法可靠计算有效边界（如 Scope 插件丢失或参数损坏），则配置级权威 Snapshot 无法建立，必须提前阻断
            if (resolution.IsBoundaryBlocked)
            {
                var diagnostic = resolution.Diagnostics.LastOrDefault();
                var message = diagnostic is null
                    ? $"The v3 plugin policy could not resolve effective boundary for folder '{folder.DisplayName}'."
                    : diagnostic.Code == NativeHistoryArtifactTransformPolicy.BlockedDiagnosticCode
                        ? I18n.GetString("BackupService_ArtifactTransformNativeHistoryNotSupported")
                        : $"{diagnostic.Code} ({diagnostic.Owner})";
                Log($"[PluginV3] {message}", LogLevel.Error);

                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });

                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = "boundary_resolution_blocked",
                    ["error"] = message,
                    ["folder"] = folder.DisplayName
                });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = "boundary_resolution_blocked",
                    ["message"] = message
                });

                var blockedOutcome = CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: message,
                    operationOutcome: OperationOutcome.Blocked);

                return new BackupTransactionExecutionResult
                {
                    Outcome = OperationOutcome.Blocked,
                    CreatedNewArchive = false,
                    SourceOutcomes = [blockedOutcome]
                };
            }
        }

        // 检查请求捕获的文件夹是否有被 V3 策略阻断捕获就绪状态的
        foreach (var folder in requestedFolders)
        {
            var resolution = resolutions[folder.Id];
            if (resolution.IsCaptureBlocked)
            {
                var diagnostic = resolution.Diagnostics.LastOrDefault();
                var message = diagnostic is null
                    ? $"The v3 plugin policy blocked backup capture for '{folder.DisplayName}'."
                    : diagnostic.Code == NativeHistoryArtifactTransformPolicy.BlockedDiagnosticCode
                        ? I18n.GetString("BackupService_ArtifactTransformNativeHistoryNotSupported")
                        : $"{diagnostic.Code} ({diagnostic.Owner})";
                Log($"[PluginV3] {message}", LogLevel.Error);

                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });

                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = "plugin_policy_blocked",
                    ["error"] = message
                });
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = "plugin_policy_blocked",
                    ["message"] = message
                });

                var blockedOutcome = CreateSourceOutcome(
                    folder,
                    BackupSourceExecutionStatus.Failed,
                    errorMessage: message,
                    operationOutcome: OperationOutcome.Blocked);

                return new BackupTransactionExecutionResult
                {
                    Outcome = OperationOutcome.Blocked,
                    CreatedNewArchive = false,
                    SourceOutcomes = [blockedOutcome]
                };
            }
        }

        // 2. 冻结配置级权威 HistoryConfigSnapshot
        var historySnapshot = new HistoryConfigSnapshot(
            new HistoryConfigId(config.Id),
            config.SourceFolders.Select(folder =>
            {
                var res = resolutions[folder.Id];
                var sourceId = new SourceId(Guid.Parse(folder.Id));
                return new HistoryConfigSourceSnapshot(
                    sourceId,
                    new SourceDescriptorSnapshot(folder.DisplayName, folder.Path),
                    res.EffectiveBoundary);
            }));

        // 3. History Boundary Preflight
        // 在真正创建归档或获取 Minecraft 快照之前，检查是否存在未参与本次捕获但管理边界发生变更的 Source。
        var plannedSourceIds = requestedFolders
            .Where(f => Guid.TryParse(f.Id, out var id) && id != Guid.Empty)
            .Select(f => new SourceId(Guid.Parse(f.Id)))
            .ToArray();

        var requiredRecaptures = await NativeHistoryCoreGateway.FindRequiredBoundaryRecapturesAsync(
            historySnapshot,
            plannedSourceIds,
            cancellationToken).ConfigureAwait(false);

        if (requiredRecaptures.Count > 0)
        {
            var driftSourceId = requiredRecaptures[0].SourceId;
            var driftFolder = config.SourceFolders.FirstOrDefault(f => Guid.TryParse(f.Id, out var id) && new SourceId(id) == driftSourceId);
            var driftName = driftFolder?.DisplayName ?? driftSourceId.ToString();
            var prevShort = requiredRecaptures[0].PreviousBoundaryFingerprint[..Math.Min(10, requiredRecaptures[0].PreviousBoundaryFingerprint.Length)];
            var currShort = requiredRecaptures[0].CurrentBoundaryFingerprint[..Math.Min(10, requiredRecaptures[0].CurrentBoundaryFingerprint.Length)];
            var driftMessage = $"配置中的“{driftName}”有效备份边界已变化，需要先通过完整配置备份重新建立可靠基线。(SourceId={driftSourceId}, previous={prevShort}, current={currShort}, requested=false)";
            Log(driftMessage, LogLevel.Warning);

            BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
            {
                ["reason"] = "boundary_drift_requires_recapture",
                ["source_id"] = driftSourceId.ToString()
            });

            return BackupTransactionExecutionResult.Blocked(driftMessage);
        }

        // 4. 发送事务级 command_started 与 command_progress（一笔备份请求只广播一次）
        BroadcastBackupLifecycle("command_started");
        BroadcastBackupLifecycle("command_progress", new Dictionary<string, string?> { ["progress"] = "0" });

        // 5. Capture / pre-durable phase。只有这一阶段的失败允许补偿删除本次 Capture 产物。
        var sourceOutcomes = new List<BackupSourceExecutionOutcome>(requestedFolders.Count);
        HistoryCommitBatch committedBatch;
        try
        {
            foreach (var folder in requestedFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolution = resolutions[folder.Id];
                var outcome = await CaptureBackupSourceAsync(
                    config,
                    folder,
                    resolution,
                    invocationOptions.Comment,
                    invocationOptions,
                    cancellationToken).ConfigureAwait(false);
                sourceOutcomes.Add(outcome);
            }

            // 如果有被取消的 Source，执行回滚清理
            if (sourceOutcomes.Any(item => item.OperationOutcome == OperationOutcome.Canceled))
            {
                await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
                BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "canceled" });
                return BackupTransactionExecutionResult.Canceled(sourceOutcomes);
            }

            // 提交到 Native History
            var results = sourceOutcomes.Select((outcome, index) =>
                EnsureCaptureResult(requestedFolders[index], resolutions[requestedFolders[index].Id].EffectiveBoundary, outcome)).ToArray();

            try
            {
                committedBatch = await NativeHistoryCoreGateway.CommitBackupAsync(
                    historySnapshot,
                    results,
                    MapInvocationKind(invocationOptions.Source),
                    startedAtUtc,
                    invocationOptions.Comment,
                    cancellationToken,
                    intent,
                    safetySnapshotIntent).ConfigureAwait(false);
            }
            catch (HistoryCommitRecoveryRequiredException ex)
            {
                // Pack 已落盘成为 durable 历史事实。此分支不得进入 pre-durable 补偿或失败终态。
                Log($"[NativeHistory] Commit pack '{ex.CommittedPackId}' is durable, but local state recovery is required: {ex.Message}", LogLevel.Warning);
                try
                {
                    await FinalizeRecoveryRequiredBackupAsync(config, requestedFolders, sourceOutcomes, ex).ConfigureAwait(false);
                }
                catch (Exception finalizationException)
                {
                    Log($"[NativeHistory] Recovery-required post-commit finalization failed: {finalizationException.Message}", LogLevel.Warning);
                }

                var recoveryOutcome = PluginBackupRequestResult.Aggregate(sourceOutcomes.Select(item => item.ToPluginResult()));
                return new BackupTransactionExecutionResult
                {
                    Outcome = recoveryOutcome is OperationOutcome.Failed or OperationOutcome.Blocked or OperationOutcome.Canceled
                        ? recoveryOutcome
                        : OperationOutcome.SuccessWithWarnings,
                    CreatedNewArchive = sourceOutcomes.Any(item => item.CreatedNewArchive),
                    SourceOutcomes = sourceOutcomes,
                    HistoryRecoveryRequired = true,
                    RecoveryMessage = ex.Message
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Pre-durable 失败：History Commit 未形成持久 Pack，清理本次事务产生的未提交归档产物
                await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
                Log($"Native History commit failed: {ex.Message}", LogLevel.Error);
                await FinalizeFailedBackupAsync(config, requestedFolders, sourceOutcomes, ex.Message).ConfigureAwait(false);
                return BackupTransactionExecutionResult.Failed(sourceOutcomes, ex.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
            BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?> { ["reason"] = "canceled" });
            return BackupTransactionExecutionResult.Canceled(sourceOutcomes);
        }
        catch (Exception ex)
        {
            await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
            Log($"Backup transaction unhandled exception: {ex.Message}", LogLevel.Error);
            await FinalizeFailedBackupAsync(config, requestedFolders, sourceOutcomes, ex.Message).ConfigureAwait(false);
            return BackupTransactionExecutionResult.Failed(sourceOutcomes, ex.Message);
        }

        // 6. Durable boundary。到达这里后 History 已经确定事实；所有辅助失败只能降级为 warning。
        bool hasPostCommitWarnings = false;
        try
        {
            hasPostCommitWarnings = await FinalizeCommittedBackupAsync(
                config,
                requestedFolders,
                sourceOutcomes,
                committedBatch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            hasPostCommitWarnings = true;
            Log($"Post-commit backup finalization failed: {ex.Message}", LogLevel.Warning);
        }

        try
        {
            CloudSyncService.QueueNativeHistorySync(
                config,
                committedBatch.NewRepresentations.Select(item => item.RepresentationId));
        }
        catch (Exception ex)
        {
            hasPostCommitWarnings = true;
            Log($"Post-commit cloud sync queueing failed: {ex.Message}", LogLevel.Warning);
        }

        if (sourceOutcomes.Any(item => item.CreatedNewArchive))
        {
            try
            {
                await PruneRetainedSourceArchivesAsync(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                hasPostCommitWarnings = true;
                Log($"Post-commit retention failed: {ex.Message}", LogLevel.Warning);
            }
        }

        var committedOutcome = PluginBackupRequestResult.Aggregate(sourceOutcomes.Select(item => item.ToPluginResult()));
        return new BackupTransactionExecutionResult
        {
            Outcome = ResolveDurableOutcome(committedOutcome, committedBatch.Run.Outcome, hasPostCommitWarnings),
            CreatedNewArchive = sourceOutcomes.Any(item => item.CreatedNewArchive),
            SourceOutcomes = sourceOutcomes,
            CommittedBatch = committedBatch
        };
    }

    private static OperationOutcome ResolveDurableOutcome(
        OperationOutcome committedOutcome,
        BackupRunOutcome runOutcome,
        bool hasPostCommitWarnings)
    {
        if (!hasPostCommitWarnings || runOutcome is BackupRunOutcome.Partial or BackupRunOutcome.Failed)
        {
            return committedOutcome;
        }

        return OperationOutcome.SuccessWithWarnings;
    }

    private static async Task<bool> FinalizeCommittedBackupAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> requestedFolders,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes,
        HistoryCommitBatch committedBatch)
    {
        int configIndex = GetConfigIndex(config);
        bool anyNewFile = false;
        bool hasPostCommitWarnings = false;
        string? latestCompletedFile = null;
        var overallOutcome = PluginBackupRequestResult.Aggregate(sourceOutcomes.Select(item => item.ToPluginResult()));

        for (int i = 0; i < requestedFolders.Count; i++)
        {
            var folder = requestedFolders[i];
            var outcome = sourceOutcomes[i];
            var task = outcome.Task;

            if (outcome.Status == BackupSourceExecutionStatus.Unavailable)
            {
                string unavailableMessage = I18n.GetString("BackupService_Folder_NoMatchingFiles");
                if (task is not null)
                {
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
                }
                Log(I18n.Format("BackupService_Log_NoMatchingFiles", folder.DisplayName), LogLevel.Warning);
                BroadcastBackupEvent(configIndex, config, folder, "backup_unavailable", new Dictionary<string, string?>
                {
                    ["reason"] = "no_matching_files"
                });
            }
            else if (outcome.CreatedNewArchive)
            {
                anyNewFile = true;
                latestCompletedFile = outcome.GeneratedFileName;
                var completedFileName = outcome.GeneratedFileName;

                // 备份完成后检查文件大小，过小时发出警告
                if (!string.IsNullOrEmpty(completedFileName) && !string.IsNullOrEmpty(config.DestinationPath))
                {
                    try
                    {
                        if (TryResolveBackupStoragePaths(config.DestinationPath, folder.DisplayName, folder.Path, out _, out var backupSubDir, out _))
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
                    }
                    catch
                    {
                    }
                }

                if (task is not null)
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_Completed");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupCompleted");
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    });
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupCompleted");
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    });
                }

                BroadcastBackupEvent(configIndex, config, folder, "backup_success", new Dictionary<string, string?>
                {
                    ["file"] = completedFileName
                });
                Log(I18n.Format("BackupService_Log_BackupSucceeded", folder.DisplayName), LogLevel.Info);
            }
            else if (outcome.Status == BackupSourceExecutionStatus.Reused)
            {
                if (task is not null)
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_NoChanges");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;
                        folder.StatusText = I18n.Format("BackupService_Task_NoChanges");
                    });
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        folder.StatusText = I18n.Format("BackupService_Task_NoChanges");
                    });
                }
                Log(I18n.Format("BackupService_Log_BackupSkippedNoChanges", folder.DisplayName), LogLevel.Info);
            }
            else
            {
                if (task is not null)
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_Failed");
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = false;
                        task.ErrorMessage = outcome.ErrorMessage;
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    });
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    });
                }
                BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
                {
                    ["error"] = outcome.ErrorMessage
                });
                NotificationService.NotifyBackupCompleted(folder.DisplayName, false, outcome.ErrorMessage);
            }
        }

        // 成功 Source 的 LastBackupTime 是否需要持久化，与整个 Run 的 terminal outcome 相互独立。
        if (anyNewFile)
        {
            var saveResult = ConfigService.SaveWithResult();
            if (!saveResult.Success)
            {
                hasPostCommitWarnings = true;
                Log($"Post-commit LastBackupTime persistence failed: {saveResult.ErrorMessage}", LogLevel.Warning);
            }
        }

        // 关键设计：严禁在存在 Failed Source 时发送 command_completed { result = "no_changes" }
        if (overallOutcome == OperationOutcome.Failed || committedBatch.Run.Outcome == BackupRunOutcome.Failed)
        {
            var firstFailed = sourceOutcomes.FirstOrDefault(o => o.Status == BackupSourceExecutionStatus.Failed);
            var errorMsg = firstFailed?.ErrorMessage ?? "Backup failed";
            BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
            {
                ["reason"] = "source_failed",
                ["error"] = errorMsg
            });
        }
        else if (anyNewFile)
        {
            BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
            {
                ["result"] = "created",
                ["file"] = latestCompletedFile
            });
        }
        else if (sourceOutcomes.All(o => o.Status == BackupSourceExecutionStatus.Unavailable))
        {
            BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
            {
                ["result"] = "unavailable",
                ["reason"] = "no_matching_files"
            });
        }
        else
        {
            BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
            {
                ["result"] = "no_changes"
            });
        }

        return hasPostCommitWarnings;
    }

    private static async Task FinalizeRecoveryRequiredBackupAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> requestedFolders,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes,
        HistoryCommitRecoveryRequiredException ex)
    {
        int configIndex = GetConfigIndex(config);
        string recoveryWarning = I18n.Format("BackupService_Log_RecoveryRequired", ex.CommittedPackId.ToString());
        for (int i = 0; i < requestedFolders.Count; i++)
        {
            var folder = requestedFolders[i];
            var outcome = sourceOutcomes[i];
            var task = outcome.Task;
            if (task is not null)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = recoveryWarning;
                    task.Progress = 100;
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = true;
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupCompleted");
                    if (outcome.CreatedNewArchive)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });
            }
            else
            {
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupCompleted");
                    if (outcome.CreatedNewArchive)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });
            }
            if (outcome.CreatedNewArchive)
            {
                BroadcastBackupEvent(configIndex, config, folder, "backup_warning", new Dictionary<string, string?>
                {
                    ["type"] = "history_recovery_required",
                    ["pack_id"] = ex.CommittedPackId.ToString(),
                    ["message"] = ex.Message
                });
            }
        }
        if (sourceOutcomes.Any(o => o.CreatedNewArchive))
        {
            ConfigService.Save();
        }
        BroadcastBackupLifecycle("command_completed", new Dictionary<string, string?>
        {
            ["result"] = "warning",
            ["reason"] = "history_recovery_required",
            ["pack_id"] = ex.CommittedPackId.ToString()
        });
    }

    private static async Task FinalizeFailedBackupAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> requestedFolders,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes,
        string errorMessage)
    {
        int configIndex = GetConfigIndex(config);
        for (int i = 0; i < requestedFolders.Count; i++)
        {
            var folder = requestedFolders[i];
            var outcome = i < sourceOutcomes.Count ? sourceOutcomes[i] : null;
            var task = outcome?.Task;
            if (task is not null)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = errorMessage;
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });
            }
            else
            {
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });
            }
            BroadcastBackupEvent(configIndex, config, folder, "backup_failed", new Dictionary<string, string?>
            {
                ["error"] = "transaction_failed",
                ["message"] = errorMessage
            });
            NotificationService.NotifyBackupCompleted(folder.DisplayName, false, errorMessage);
        }
        BroadcastBackupLifecycle("command_failed", new Dictionary<string, string?>
        {
            ["reason"] = "transaction_failed",
            ["error"] = errorMessage
        });
    }
}
