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

    private sealed record BackupTerminalLifecycle(
        string EventName,
        IReadOnlyDictionary<string, string?> Fields);

    private sealed class BackupTerminalLifecycleScope(BackupTerminalLifecycle initial) : IDisposable
    {
        private BackupTerminalLifecycle _terminal = initial;
        private int _disposed;

        public void Set(BackupTerminalLifecycle terminal)
            => _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                BroadcastBackupLifecycle(_terminal.EventName, _terminal.Fields);
            }
            catch (Exception ex)
            {
                Log($"Backup terminal lifecycle broadcast failed: {ex.Message}", LogLevel.Warning);
            }
        }
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
    /// 8. 保持 Failed/Blocked/Canceled 聚合结果不被 post-commit warning 覆盖；其余结果可降级为 SuccessWithWarnings。
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
        using var terminalLifecycle = new BackupTerminalLifecycleScope(
            CreateFailedTerminal("transaction_interrupted", "Backup transaction was interrupted."));
        BroadcastBackupLifecycle("command_progress", new Dictionary<string, string?> { ["progress"] = "0" });

        // command_started 之后的唯一 terminal lifecycle 由作用域 owner 在方法退出时发送。
        // 默认值仅用于防御未预期异常；所有正常返回路径都会先设置更具体的终态。
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
                terminalLifecycle.Set(CreateCanceledTerminal());
                await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
                await FinalizeCanceledBackupAsync(requestedFolders, sourceOutcomes).ConfigureAwait(false);
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
                // Pack 已落盘成为 durable 历史事实。Recovery warning 叠加在 Source 原结果之上。
                Log($"[NativeHistory] Commit pack '{ex.CommittedPackId}' is durable, but local state recovery is required: {ex.Message}", LogLevel.Warning);
                var recoveryOutcome = PluginBackupRequestResult.Aggregate(sourceOutcomes.Select(item => item.ToPluginResult()));
                terminalLifecycle.Set(CreateRecoveryRequiredTerminal(recoveryOutcome, sourceOutcomes, ex));
                try
                {
                    _ = await FinalizeCommittedBackupAsync(
                        config,
                        requestedFolders,
                        sourceOutcomes,
                        ex).ConfigureAwait(false);
                }
                catch (Exception finalizationException)
                {
                    Log($"[NativeHistory] Recovery-required post-commit finalization failed: {finalizationException.Message}", LogLevel.Warning);
                }

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
                terminalLifecycle.Set(CreateFailedTerminal("transaction_failed", ex.Message));
                await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
                Log($"Native History commit failed: {ex.Message}", LogLevel.Error);
                await FinalizeFailedBackupAsync(config, requestedFolders, sourceOutcomes, ex.Message).ConfigureAwait(false);
                return BackupTransactionExecutionResult.Failed(sourceOutcomes, ex.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            terminalLifecycle.Set(CreateCanceledTerminal());
            await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
            await FinalizeCanceledBackupAsync(requestedFolders, sourceOutcomes).ConfigureAwait(false);
            return BackupTransactionExecutionResult.Canceled(sourceOutcomes);
        }
        catch (Exception ex)
        {
            terminalLifecycle.Set(CreateFailedTerminal("transaction_failed", ex.Message));
            await CleanupUncommittedOutcomesAsync(sourceOutcomes).ConfigureAwait(false);
            Log($"Backup transaction unhandled exception: {ex.Message}", LogLevel.Error);
            await FinalizeFailedBackupAsync(config, requestedFolders, sourceOutcomes, ex.Message).ConfigureAwait(false);
            return BackupTransactionExecutionResult.Failed(sourceOutcomes, ex.Message);
        }

        // 6. Durable boundary。到达这里后 History 已经确定事实；所有辅助失败只能降级为 warning。
        var committedOutcome = PluginBackupRequestResult.Aggregate(sourceOutcomes.Select(item => item.ToPluginResult()));
        terminalLifecycle.Set(CreateCommittedTerminal(committedOutcome, sourceOutcomes, hasPostCommitWarnings: false));
        bool hasPostCommitWarnings = false;
        try
        {
            hasPostCommitWarnings = await FinalizeCommittedBackupAsync(
                config,
                requestedFolders,
                sourceOutcomes).ConfigureAwait(false);
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

        var durableOutcome = ResolveDurableOutcome(committedOutcome, hasPostCommitWarnings);
        terminalLifecycle.Set(CreateCommittedTerminal(durableOutcome, sourceOutcomes, hasPostCommitWarnings));
        return new BackupTransactionExecutionResult
        {
            Outcome = durableOutcome,
            CreatedNewArchive = sourceOutcomes.Any(item => item.CreatedNewArchive),
            SourceOutcomes = sourceOutcomes,
            CommittedBatch = committedBatch
        };
    }

    private static BackupTerminalLifecycle CreateCanceledTerminal()
        => new(
            "command_failed",
            new Dictionary<string, string?> { ["reason"] = "canceled" });

    private static BackupTerminalLifecycle CreateFailedTerminal(string reason, string error)
        => new(
            "command_failed",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["error"] = error
            });

    private static BackupTerminalLifecycle CreateCommittedTerminal(
        OperationOutcome outcome,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes,
        bool hasPostCommitWarnings)
    {
        if (outcome is OperationOutcome.Failed or OperationOutcome.Blocked or OperationOutcome.Canceled)
        {
            var firstFailed = sourceOutcomes.FirstOrDefault(item => item.Status == BackupSourceExecutionStatus.Failed);
            var firstUnavailable = sourceOutcomes.FirstOrDefault(item => item.Status == BackupSourceExecutionStatus.Unavailable);
            return firstFailed is not null
                ? CreateFailedTerminal("source_failed", firstFailed.ErrorMessage)
                : CreateFailedTerminal("source_unavailable", firstUnavailable?.ErrorMessage ?? "Backup source is unavailable.");
        }

        if (hasPostCommitWarnings)
        {
            return new BackupTerminalLifecycle(
                "command_completed",
                new Dictionary<string, string?>
                {
                    ["result"] = "warning",
                    ["reason"] = "post_commit_warning"
                });
        }

        var latestCreated = sourceOutcomes.LastOrDefault(item => item.CreatedNewArchive)?.GeneratedFileName;
        if (sourceOutcomes.Any(item => item.CreatedNewArchive))
        {
            return new BackupTerminalLifecycle(
                "command_completed",
                new Dictionary<string, string?>
                {
                    ["result"] = "created",
                    ["file"] = latestCreated
                });
        }

        return new BackupTerminalLifecycle(
            "command_completed",
            new Dictionary<string, string?> { ["result"] = "no_changes" });
    }

    private static BackupTerminalLifecycle CreateRecoveryRequiredTerminal(
        OperationOutcome outcome,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes,
        HistoryCommitRecoveryRequiredException ex)
    {
        if (outcome is OperationOutcome.Failed or OperationOutcome.Blocked or OperationOutcome.Canceled)
        {
            var firstFailed = sourceOutcomes.FirstOrDefault(item => item.Status == BackupSourceExecutionStatus.Failed);
            var firstUnavailable = sourceOutcomes.FirstOrDefault(item => item.Status == BackupSourceExecutionStatus.Unavailable);
            return new BackupTerminalLifecycle(
                "command_failed",
                new Dictionary<string, string?>
                {
                    ["reason"] = firstFailed is not null ? "source_failed" : "source_unavailable",
                    ["error"] = firstFailed?.ErrorMessage
                        ?? firstUnavailable?.ErrorMessage
                        ?? "Backup failed",
                    ["history_recovery_required"] = "true",
                    ["pack_id"] = ex.CommittedPackId.ToString()
                });
        }

        return new BackupTerminalLifecycle(
            "command_completed",
            new Dictionary<string, string?>
            {
                ["result"] = "warning",
                ["reason"] = "history_recovery_required",
                ["pack_id"] = ex.CommittedPackId.ToString()
            });
    }

    private static OperationOutcome ResolveDurableOutcome(
        OperationOutcome committedOutcome,
        bool hasPostCommitWarnings)
    {
        if (committedOutcome is OperationOutcome.Failed or OperationOutcome.Blocked or OperationOutcome.Canceled)
        {
            return committedOutcome;
        }

        return hasPostCommitWarnings
            ? OperationOutcome.SuccessWithWarnings
            : committedOutcome;
    }

    private static async Task<bool> FinalizeCommittedBackupAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> requestedFolders,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes,
        HistoryCommitRecoveryRequiredException? recoveryRequired = null)
    {
        int configIndex = GetConfigIndex(config);
        bool anyNewFile = sourceOutcomes.Any(item => item.CreatedNewArchive);
        bool hasPostCommitWarnings = false;
        string? recoveryWarning = recoveryRequired is null
            ? null
            : I18n.Format("BackupService_Log_RecoveryRequired", recoveryRequired.CommittedPackId.ToString());

        for (int i = 0; i < requestedFolders.Count; i++)
        {
            var folder = requestedFolders[i];
            var outcome = sourceOutcomes[i];
            var task = outcome.Task;

            try
            {
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
                            task.IsSuccess = false;
                            task.ErrorMessage = outcome.ErrorMessage;
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
                            task.Status = recoveryWarning ?? I18n.Format("BackupService_Task_Completed");
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

                    if (recoveryRequired is null)
                    {
                        BroadcastBackupEvent(configIndex, config, folder, "backup_success", new Dictionary<string, string?>
                        {
                            ["file"] = completedFileName
                        });
                        NotificationService.NotifyBackupCompleted(folder.DisplayName, true);
                    }
                    else
                    {
                        BroadcastBackupEvent(configIndex, config, folder, "backup_warning", new Dictionary<string, string?>
                        {
                            ["type"] = "history_recovery_required",
                            ["pack_id"] = recoveryRequired.CommittedPackId.ToString(),
                            ["message"] = recoveryRequired.Message
                        });
                    }
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
                    var failureFields = new Dictionary<string, string?>
                    {
                        ["error"] = outcome.ErrorMessage
                    };
                    if (recoveryRequired is not null)
                    {
                        failureFields["history_recovery_required"] = "true";
                        failureFields["pack_id"] = recoveryRequired.CommittedPackId.ToString();
                    }
                    BroadcastBackupEvent(configIndex, config, folder, "backup_failed", failureFields);
                    NotificationService.NotifyBackupCompleted(folder.DisplayName, false, outcome.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                hasPostCommitWarnings = true;
                Log($"Post-commit finalization failed for folder '{folder.DisplayName}': {ex.Message}", LogLevel.Warning);
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

        return hasPostCommitWarnings;
    }

    private static async Task FinalizeCanceledBackupAsync(
        IReadOnlyList<ManagedFolder> requestedFolders,
        IReadOnlyList<BackupSourceExecutionOutcome> sourceOutcomes)
    {
        string canceledMessage = I18n.GetString("Common_Canceled");
        for (int i = 0; i < sourceOutcomes.Count && i < requestedFolders.Count; i++)
        {
            var folder = requestedFolders[i];
            var task = sourceOutcomes[i].Task;
            if (task is null)
            {
                continue;
            }

            try
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = canceledMessage;
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = canceledMessage;
                    folder.StatusText = canceledMessage;
                });
            }
            catch (Exception ex)
            {
                Log($"Canceled backup finalization failed for folder '{folder.DisplayName}': {ex.Message}", LogLevel.Warning);
            }
        }
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
            try
            {
                await FinalizeFailedSourceAsync(configIndex, config, folder, outcome?.Task, errorMessage).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Failed backup finalization failed for folder '{folder.DisplayName}': {ex.Message}", LogLevel.Warning);
            }
        }
    }

    private static async Task FinalizeFailedSourceAsync(
        int configIndex,
        BackupConfig config,
        ManagedFolder folder,
        BackupTask? task,
        string errorMessage)
    {
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
}
