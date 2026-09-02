using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 自动备份调度：维护两条可取消异步循环——60 秒的调度/间隔轮询与
    /// 10 秒的条件轮询（文件解锁触发）。
    /// 触发的备份经配置级/文件夹级运行态互斥排队执行，避免同一配置的自动备份重叠；
    /// 循环随配置保存动态启停。
    /// </summary>
    public static class AutomationService
    {
        private static readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private static readonly AutomationConditionStateTracker _conditionStates = new();
        private static readonly object _runStateLock = new();
        private static readonly HashSet<string> _activeConfigRuns = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _activeFolderRuns = new(StringComparer.OrdinalIgnoreCase);
        private static TimeProvider _timeProvider = TimeProvider.System;
        private static AsyncPeriodicLoop? _scheduleLoop;
        private static AsyncPeriodicLoop? _conditionLoop;
        private static CancellationTokenSource? _stopSource;
        private static Task _startupBackupsTask = Task.CompletedTask;
        private static bool _isRunning;

        public static Task StartAsync(CancellationToken cancellationToken = default)
            => StartAsync(TimeProvider.System, cancellationToken);

        internal static async Task StartAsync(
            TimeProvider timeProvider,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isRunning)
                    return;

                _timeProvider = timeProvider;
                _stopSource = new CancellationTokenSource();
                _scheduleLoop = new AsyncPeriodicLoop(
                    _timeProvider,
                    TimeSpan.FromSeconds(60),
                    runImmediately: true,
                    RunScheduleRoundAsync,
                    ex => LogLoopFailure("schedule", ex));
                _conditionLoop = new AsyncPeriodicLoop(
                    _timeProvider,
                    TimeSpan.FromSeconds(10),
                    runImmediately: false,
                    RunConditionRoundAsync,
                    ex => LogLoopFailure("condition", ex));
                _isRunning = true;
                ConfigService.Saved += OnConfigSaved;

                await EvaluateLoopsAsync(_stopSource.Token).ConfigureAwait(false);
                _startupBackupsTask = RunStartupBackupsAsync(_stopSource.Token);
            }
            catch
            {
                ConfigService.Saved -= OnConfigSaved;
                _isRunning = false;
                _stopSource?.Cancel();
                if (_scheduleLoop is not null)
                    await _scheduleLoop.StopAsync().ConfigureAwait(false);
                if (_conditionLoop is not null)
                    await _conditionLoop.StopAsync().ConfigureAwait(false);
                _stopSource?.Dispose();
                _stopSource = null;
                _scheduleLoop = null;
                _conditionLoop = null;
                throw;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public static async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_isRunning)
                    return;

                _isRunning = false;
                ConfigService.Saved -= OnConfigSaved;
                _stopSource?.Cancel();

                if (_scheduleLoop is not null)
                    await _scheduleLoop.StopAsync().ConfigureAwait(false);
                if (_conditionLoop is not null)
                    await _conditionLoop.StopAsync().ConfigureAwait(false);
                await _startupBackupsTask.ConfigureAwait(false);

                _conditionStates.Clear();
                lock (_runStateLock)
                {
                    _activeConfigRuns.Clear();
                    _activeFolderRuns.Clear();
                }

                _stopSource?.Dispose();
                _stopSource = null;
                _scheduleLoop = null;
                _conditionLoop = null;
                _startupBackupsTask = Task.CompletedTask;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private static async Task RunStartupBackupsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var configs = await CaptureStartupConfigsAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var tasks = configs.Select(config => QueueAutoBackupAsync(
                    config.Config,
                    I18n.GetString("AutoBackup_Reason_AppStart"),
                    config.TargetFolder,
                    config.RequiresSingleFolder,
                    updateAutomationState: true));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogLoopFailure("startup", ex);
            }
        }

        // ── Loop lifecycle ──────────────────────────────────────────

        private static async Task EvaluateLoopsAsync(CancellationToken cancellationToken)
        {
            var requirements = await CaptureLoopRequirementsAsync(cancellationToken).ConfigureAwait(false);
            if (_conditionLoop is not null)
            {
                if (requirements.NeedsConditionLoop)
                    await _conditionLoop.StartAsync(_stopSource?.Token ?? cancellationToken).ConfigureAwait(false);
                else
                    await _conditionLoop.StopAsync().ConfigureAwait(false);
            }

            if (_scheduleLoop is not null)
            {
                if (requirements.NeedsScheduleLoop)
                    await _scheduleLoop.StartAsync(_stopSource?.Token ?? cancellationToken).ConfigureAwait(false);
                else
                    await _scheduleLoop.StopAsync().ConfigureAwait(false);
            }
        }

        private static void OnConfigSaved()
        {
            _ = RefreshLoopRequirementsAsync();
        }

        private static async Task RefreshLoopRequirementsAsync()
        {
            try
            {
                await _lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_isRunning && _stopSource is { IsCancellationRequested: false })
                        await EvaluateLoopsAsync(_stopSource.Token).ConfigureAwait(false);
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            catch (OperationCanceledException) when (_stopSource?.IsCancellationRequested != false)
            {
            }
            catch (Exception ex)
            {
                LogLoopFailure("configuration refresh", ex);
            }
        }

        private static void LogLoopFailure(string loopName, Exception exception)
        {
            LogService.LogError(
                $"[AutomationService] The {loopName} loop iteration failed: {exception.Message}",
                nameof(AutomationService),
                exception);
        }

        // ── Loop iterations ─────────────────────────────────────────

        /// <summary>
        /// 调度/间隔轮询（每 60 秒）：先处理计划任务条目，同一配置每轮只触发
        /// 第一个命中的条目且 2 分钟内不重复触发；计划已触发则本轮跳过间隔判断。
        /// 间隔模式按"距上次自动备份的分钟数"到期触发，间隔被钳制在 1–10080 分钟。
        /// 周期循环串行等待本轮备份完成，慢任务不会与下一轮重叠。
        /// </summary>
        private static async Task RunScheduleRoundAsync(CancellationToken cancellationToken)
        {
            var nowLocal = _timeProvider.GetLocalNow().LocalDateTime;
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var configs = await CaptureScheduleConfigsAsync(cancellationToken).ConfigureAwait(false);
            var backupTasks = new List<Task>();

            foreach (var config in configs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = AutomationSchedulePolicy.Evaluate(
                    nowLocal,
                    utcNow,
                    config.ScheduledMode,
                    config.ScheduleEntries,
                    config.IntervalMode,
                    config.IntervalMinutes,
                    config.LastAutoBackupUtc);
                if (decision.Kind == AutomationTriggerKind.None)
                    continue;

                string reason;
                if (decision.Kind == AutomationTriggerKind.Scheduled)
                {
                    await MarkScheduleTriggeredAsync(
                        config.Config.Id,
                        decision.ScheduleEntryIndex,
                        utcNow,
                        cancellationToken).ConfigureAwait(false);
                    reason = I18n.Format(
                        "AutoBackup_Reason_Scheduled",
                        FormatScheduleDescription(config.ScheduleEntries[decision.ScheduleEntryIndex]));
                }
                else
                {
                    reason = I18n.Format("AutoBackup_Reason_Interval", decision.IntervalMinutes);
                }

                backupTasks.Add(QueueAutoBackupAsync(
                    config.Config,
                    reason,
                    config.TargetFolder,
                    config.RequiresSingleFolder,
                    updateAutomationState: true));
            }

            await Task.WhenAll(backupTasks).ConfigureAwait(false);
        }

        /// <summary>
        /// 条件轮询（每 10 秒）：按"配置|文件夹|相对路径"三元组跟踪条件文件的状态
        /// （Missing/Locked/Unlocked），仅在状态迁移时动作（Locked→Unlocked 触发备份）；
        /// 首次观测只记录基线不触发。轮末清理不再活跃的三元组，防止配置删除后残留。
        /// </summary>
        private static async Task RunConditionRoundAsync(CancellationToken cancellationToken)
        {
            var configs = await CaptureConditionConfigsAsync(cancellationToken).ConfigureAwait(false);
            var activeStateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backupTasks = new List<Task>();

            foreach (var config in configs)
            {
                foreach (var folder in config.Folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stateKey = BuildConditionStateKey(config.Config.Id, folder.Path, config.RelativePath);
                    activeStateKeys.Add(stateKey);
                    var filePath = TryBuildConditionFilePath(folder, config.RelativePath);
                    var transition = _conditionStates.Observe(stateKey, EvaluateConditionFileState(filePath));
                    if (transition == AutomationConditionTransition.BecameUnlocked)
                    {
                        LogService.Log(I18n.Format(
                            "AutoBackup_Log_ConditionUnlocked",
                            config.Config.Name,
                            GetFolderDisplayName(folder),
                            config.RelativePath));
                        backupTasks.Add(QueueAutoBackupAsync(
                            config.Config,
                            I18n.Format("AutoBackup_Reason_FileUnlocked", config.RelativePath),
                            folder,
                            requiresSingleFolder: false,
                            updateAutomationState: config.UpdateAutomationState));
                    }
                    else if (transition == AutomationConditionTransition.BecameLocked)
                    {
                        LogService.Log(I18n.Format(
                            "AutoBackup_Log_ConditionLocked",
                            config.Config.Name,
                            GetFolderDisplayName(folder),
                            config.RelativePath));
                    }
                }
            }

            _conditionStates.RetainOnly(activeStateKeys);
            await Task.WhenAll(backupTasks).ConfigureAwait(false);
        }

        private static Task<LoopRequirements> CaptureLoopRequirementsAsync(CancellationToken cancellationToken)
            => UiDispatcherService.RunOnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configs = ConfigService.CurrentConfig?.BackupConfigs ?? [];
                var needsConditionLoop = configs.Any(config =>
                    config?.Automation is
                    {
                        AutoBackupEnabled: true,
                        ConditionalModeEnabled: true,
                        ConditionType: AutomationConditionType.FileUnlocked
                    });
                var needsScheduleLoop = configs.Any(config =>
                    config?.Automation?.AutoBackupEnabled == true
                    && (config.Automation.ScheduledMode || config.Automation.IntervalMode));
                return Task.FromResult(new LoopRequirements(needsScheduleLoop, needsConditionLoop));
            });

        private static Task<IReadOnlyList<AutomationInvocationSnapshot>> CaptureStartupConfigsAsync(
            CancellationToken cancellationToken)
            => UiDispatcherService.RunOnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<AutomationInvocationSnapshot> snapshots =
                    (ConfigService.CurrentConfig?.BackupConfigs ?? [])
                    .Where(config => config?.Automation is { AutoBackupEnabled: true, RunOnAppStart: true })
                    .Select(CreateInvocationSnapshot)
                    .ToArray();
                return Task.FromResult(snapshots);
            });

        private static Task<IReadOnlyList<ScheduleConfigSnapshot>> CaptureScheduleConfigsAsync(
            CancellationToken cancellationToken)
            => UiDispatcherService.RunOnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<ScheduleConfigSnapshot> snapshots =
                    (ConfigService.CurrentConfig?.BackupConfigs ?? [])
                    .Where(config => config?.Automation?.AutoBackupEnabled == true)
                    .Select(config =>
                    {
                        config.Automation.Normalize(config.SourceFolders);
                        var invocation = CreateInvocationSnapshot(config);
                        return new ScheduleConfigSnapshot(
                            config,
                            invocation.TargetFolder,
                            invocation.RequiresSingleFolder,
                            config.Automation.ScheduledMode,
                            config.Automation.ScheduleEntries.Select(entry => new AutomationScheduleCandidate(
                                entry.MonthSelection,
                                entry.DaySelection,
                                entry.Hour,
                                entry.Minute,
                                entry.LastTriggeredUtc)).ToArray(),
                            config.Automation.IntervalMode,
                            config.Automation.IntervalMinutes,
                            config.Automation.LastAutoBackupUtc);
                    })
                    .ToArray();
                return Task.FromResult(snapshots);
            });

        private static Task<IReadOnlyList<ConditionConfigSnapshot>> CaptureConditionConfigsAsync(
            CancellationToken cancellationToken)
            => UiDispatcherService.RunOnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<ConditionConfigSnapshot> snapshots =
                    (ConfigService.CurrentConfig?.BackupConfigs ?? [])
                    .Where(config => config?.Automation is
                    {
                        AutoBackupEnabled: true,
                        ConditionalModeEnabled: true,
                        ConditionType: AutomationConditionType.FileUnlocked
                    })
                    .Select(config =>
                    {
                        config.Automation.Normalize(config.SourceFolders);
                        return new ConditionConfigSnapshot(
                            config,
                            NormalizeConditionRelativePath(config.Automation.ConditionRelativePath),
                            ResolveConditionFolders(config).ToArray(),
                            config.Automation.Scope == AutomationScope.SingleFolder);
                    })
                    .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.RelativePath))
                    .ToArray();
                return Task.FromResult(snapshots);
            });

        private static AutomationInvocationSnapshot CreateInvocationSnapshot(BackupConfig config)
        {
            config.Automation.Normalize(config.SourceFolders);
            var requiresSingleFolder = config.Automation.Scope == AutomationScope.SingleFolder;
            return new AutomationInvocationSnapshot(
                config,
                requiresSingleFolder ? ResolveSingleTargetFolder(config) : null,
                requiresSingleFolder);
        }

        private static async Task MarkScheduleTriggeredAsync(
            string configId,
            int scheduleEntryIndex,
            DateTime triggeredUtc,
            CancellationToken cancellationToken)
        {
            var result = await ConfigService.UpdateAndSaveAsync(current =>
            {
                var liveConfig = current.BackupConfigs.FirstOrDefault(config =>
                    string.Equals(config.Id, configId, StringComparison.OrdinalIgnoreCase));
                if (liveConfig is null
                    || scheduleEntryIndex < 0
                    || scheduleEntryIndex >= liveConfig.Automation.ScheduleEntries.Count)
                    return;
                liveConfig.Automation.ScheduleEntries[scheduleEntryIndex].LastTriggeredUtc = triggeredUtc;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                LogService.LogWarning(
                    $"Failed to persist scheduled automation trigger for '{configId}': {result.ErrorMessage}",
                    nameof(AutomationService));
            }
        }

        private static IEnumerable<ManagedFolder> ResolveConditionFolders(BackupConfig config)
        {
            if (config.SourceFolders == null || config.SourceFolders.Count == 0)
            {
                yield break;
            }

            if (config.Automation.Scope == AutomationScope.SingleFolder)
            {
                var targetFolder = ResolveSingleTargetFolder(config);
                if (targetFolder != null)
                {
                    yield return targetFolder;
                }

                yield break;
            }

            foreach (var folder in config.SourceFolders)
            {
                if (folder != null && !string.IsNullOrWhiteSpace(folder.Path))
                {
                    yield return folder;
                }
            }
        }

        private static ManagedFolder? ResolveSingleTargetFolder(BackupConfig config)
        {
            if (config.SourceFolders == null || config.SourceFolders.Count == 0)
            {
                return null;
            }

            string targetFolderPath = config.Automation.TargetFolderPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetFolderPath))
            {
                return null;
            }

            return config.SourceFolders.FirstOrDefault(folder =>
                folder != null &&
                !string.IsNullOrWhiteSpace(folder.Path) &&
                string.Equals(folder.Path, targetFolderPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 排队执行一次自动备份：SingleFolder 作用域先解析目标文件夹（缺失即放弃），
        /// 再经 TryEnterRunState 互斥进入运行态，执行完毕 finally 释放。
        /// </summary>
        private static async Task QueueAutoBackupAsync(
            BackupConfig config,
            string reason,
            ManagedFolder? targetFolder,
            bool requiresSingleFolder,
            bool updateAutomationState)
        {
            ManagedFolder? effectiveTargetFolder = targetFolder;

            if (effectiveTargetFolder == null && requiresSingleFolder)
            {
                LogService.Log(I18n.Format("AutoBackup_Log_SingleTargetMissing", config.Name));
                return;
            }

            if (!TryEnterRunState(config, effectiveTargetFolder))
            {
                return;
            }

            try
            {
                await RunAutoBackupAsync(
                    config,
                    reason,
                    effectiveTargetFolder,
                    updateAutomationState);
            }
            finally
            {
                ExitRunState(config, effectiveTargetFolder);
            }
        }

        /// <summary>
        /// 尝试进入自动备份运行态（配置级与文件夹级互斥）：
        /// 整配置运行（targetFolder=null）要求该配置没有任何进行中的运行；
        /// 单文件夹运行要求配置级无运行且同文件夹无运行。已占用时返回 false 静默放弃。
        /// </summary>
        private static bool TryEnterRunState(BackupConfig config, ManagedFolder? targetFolder)
        {
            lock (_runStateLock)
            {
                string configKey = config.Id ?? string.Empty;
                string folderPrefix = configKey + "|";

                if (targetFolder == null)
                {
                    if (_activeConfigRuns.Contains(configKey) ||
                        _activeFolderRuns.Any(key => key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }

                    _activeConfigRuns.Add(configKey);
                    return true;
                }

                string folderKey = BuildFolderRunKey(configKey, targetFolder.Path);
                if (_activeConfigRuns.Contains(configKey) || _activeFolderRuns.Contains(folderKey))
                {
                    return false;
                }

                _activeFolderRuns.Add(folderKey);
                return true;
            }
        }

        /// <summary>
        /// 退出运行态，释放对应的配置级或文件夹级占用。
        /// </summary>
        private static void ExitRunState(BackupConfig config, ManagedFolder? targetFolder)
        {
            lock (_runStateLock)
            {
                string configKey = config.Id ?? string.Empty;
                if (targetFolder == null)
                {
                    _activeConfigRuns.Remove(configKey);
                    return;
                }

                _activeFolderRuns.Remove(BuildFolderRunKey(configKey, targetFolder.Path));
            }
        }

        private static string BuildFolderRunKey(string configId, string? folderPath)
        {
            return $"{configId}|{folderPath?.Trim() ?? string.Empty}";
        }

        private static string BuildConditionStateKey(string configId, string? folderPath, string relativePath)
        {
            return $"{configId}|{folderPath?.Trim() ?? string.Empty}|{relativePath}";
        }

        private static string NormalizeConditionRelativePath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            string normalized = relativePath.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            while (normalized.Contains(new string(Path.DirectorySeparatorChar, 2), StringComparison.Ordinal))
            {
                normalized = normalized.Replace(
                    new string(Path.DirectorySeparatorChar, 2),
                    Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal);
            }

            return normalized.TrimStart(Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// 在文件夹根内解析条件文件路径：拒绝根路径输入，展开后必须仍位于文件夹根内
        /// （路径穿越守卫），否则返回 null。
        /// </summary>
        private static string? TryBuildConditionFilePath(ManagedFolder folder, string relativePath)
        {
            if (folder == null || string.IsNullOrWhiteSpace(folder.Path) || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            if (Path.IsPathRooted(relativePath))
            {
                return null;
            }

            try
            {
                string folderRoot = Path.GetFullPath(folder.Path);
                string candidatePath = Path.GetFullPath(Path.Combine(folderRoot, relativePath));
                string normalizedRoot = folderRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                if (!candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(candidatePath, folderRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return candidatePath;
            }
            catch
            {
                return null;
            }
        }

        private static AutomationConditionFileState EvaluateConditionFileState(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return AutomationConditionFileState.Missing;
            }

            return FileLockService.IsFileLocked(filePath)
                ? AutomationConditionFileState.Locked
                : AutomationConditionFileState.Unlocked;
        }

        private static string FormatScheduleDescription(AutomationScheduleCandidate entry)
        {
            string month = entry.MonthSelection == 0 ? "*" : entry.MonthSelection.ToString();
            string day = entry.DaySelection == 0 ? "*" : entry.DaySelection.ToString();
            return $"{month}/{day} {entry.Hour:D2}:{entry.Minute:D2}";
        }

        /// <summary>
        /// 执行一次自动备份并更新自动化状态：记录 LastAutoBackupUtc、应用无变化停用策略、
        /// 保存配置；异常只记录与通知，不向外抛出。
        /// </summary>
        private static async Task RunAutoBackupAsync(
            BackupConfig config,
            string reason,
            ManagedFolder? targetFolder,
            bool updateAutomationState)
        {
            try
            {
                if (targetFolder == null)
                {
                    LogService.Log(I18n.Format("AutoBackup_Log_TriggeredConfig", reason, config.Name));
                }
                else
                {
                    LogService.Log(I18n.Format(
                        "AutoBackup_Log_TriggeredFolder",
                        reason,
                        config.Name,
                        GetFolderDisplayName(targetFolder)));
                }
            }
            catch
            {
            }

            try
            {
                bool hadChanges = targetFolder == null
                    ? await BackupService.BackupConfigAsync(config, BackupInvocationOptions.ForAutomatic())
                    : await BackupService.BackupFolderAsync(
                        config,
                        targetFolder,
                        invocationOptions: BackupInvocationOptions.ForAutomatic());

                if (updateAutomationState)
                {
                    var saveResult = await ConfigService.UpdateAndSaveAsync(current =>
                    {
                        var liveConfig = current.BackupConfigs.FirstOrDefault(item =>
                            string.Equals(item.Id, config.Id, StringComparison.OrdinalIgnoreCase));
                        if (liveConfig is null)
                        {
                            return;
                        }

                        liveConfig.Automation.LastAutoBackupUtc = _timeProvider.GetUtcNow().UtcDateTime;
                        ApplyNoChangeStopPolicy(liveConfig, hadChanges);
                    }).ConfigureAwait(false);
                    if (!saveResult.Success)
                    {
                        LogService.LogWarning(
                            $"Failed to persist automation state for '{config.Name}': {saveResult.ErrorMessage}",
                            nameof(AutomationService));
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    LogService.Log(I18n.Format("AutoBackup_Log_Failed", config.Name, ex.Message));
                    NotificationService.ShowError(I18n.Format("AutoBackup_Notification_Failed", config.Name, ex.Message));
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 无变化停用策略：连续 N 次自动备份都无变更时自动关闭该配置的自动备份。
        /// 例外：任一源文件夹的 level.dat（Minecraft 存档锁文件）仍被锁定时本轮跳过停用
        /// ——游戏仍在运行、变更大概率还会出现，待下一轮再检查。
        /// </summary>
        private static void ApplyNoChangeStopPolicy(BackupConfig config, bool hadChanges)
        {
            if (!config.Automation.StopAfterNoChangeEnabled)
            {
                return;
            }

            if (hadChanges)
            {
                config.Automation.ConsecutiveNoChangeCount = 0;
                return;
            }

            config.Automation.ConsecutiveNoChangeCount++;
            if (config.Automation.ConsecutiveNoChangeCount < config.Automation.StopAfterNoChangeCount)
            {
                return;
            }

            foreach (var folder in config.SourceFolders)
            {
                if (FileLockService.IsFileLocked(Path.Combine(folder.Path, "level.dat")))
                {
                    LogService.Log(I18n.Format("AutoBackup_Log_StopSkippedBecauseLocked", config.Name));
                    return;
                }
            }

            config.Automation.AutoBackupEnabled = false;
            LogService.Log(I18n.Format(
                "AutoBackup_Log_DisabledNoChanges",
                config.Name,
                config.Automation.ConsecutiveNoChangeCount));

            try
            {
                NotificationService.ShowImportant(
                    I18n.Format(
                        "AutoBackup_StoppedNoChanges",
                        config.Name,
                        config.Automation.ConsecutiveNoChangeCount.ToString()));
            }
            catch
            {
            }

            config.Automation.ConsecutiveNoChangeCount = 0;
        }

        private sealed record LoopRequirements(bool NeedsScheduleLoop, bool NeedsConditionLoop);

        private sealed record AutomationInvocationSnapshot(
            BackupConfig Config,
            ManagedFolder? TargetFolder,
            bool RequiresSingleFolder);

        private sealed record ScheduleConfigSnapshot(
            BackupConfig Config,
            ManagedFolder? TargetFolder,
            bool RequiresSingleFolder,
            bool ScheduledMode,
            IReadOnlyList<AutomationScheduleCandidate> ScheduleEntries,
            bool IntervalMode,
            int IntervalMinutes,
            DateTime LastAutoBackupUtc);

        private sealed record ConditionConfigSnapshot(
            BackupConfig Config,
            string RelativePath,
            IReadOnlyList<ManagedFolder> Folders,
            bool UpdateAutomationState);

        private static string GetFolderDisplayName(ManagedFolder folder)
        {
            if (!string.IsNullOrWhiteSpace(folder.DisplayName))
            {
                return folder.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(folder.Path))
            {
                return Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return string.Empty;
        }
    }
}
