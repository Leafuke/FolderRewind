using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 自动备份调度：维护两条独立定时器——60 秒的调度/间隔定时器（OnTick）与
    /// 10 秒的条件轮询定时器（OnConditionTick，文件解锁触发）。
    /// 触发的备份经配置级/文件夹级运行态互斥排队执行，避免同一配置的自动备份重叠；
    /// 定时器随配置保存动态启停。
    /// </summary>
    public static class AutomationService
    {
        private static Timer? _scheduleTimer;
        private static Timer? _conditionTimer;
        private static bool _isRunning;
        private static bool _scheduleTimerEnabled;
        private static bool _conditionTimerEnabled;

        private static readonly SemaphoreSlim _tickLock = new(1, 1);
        private static readonly SemaphoreSlim _conditionTickLock = new(1, 1);

        private static readonly ConcurrentDictionary<string, ConditionFileState> _conditionStates
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly object _runStateLock = new();
        private static readonly HashSet<string> _activeConfigRuns = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _activeFolderRuns = new(StringComparer.OrdinalIgnoreCase);

        private enum ConditionFileState
        {
            Missing = 0,
            Locked = 1,
            Unlocked = 2
        }

        public static void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;

            EvaluateTimers();
            ConfigService.Saved += OnConfigSaved;

            CheckStartupBackups();
        }

        public static void Stop()
        {
            ConfigService.Saved -= OnConfigSaved;

            StopScheduleTimer();
            StopConditionTimer();

            _conditionStates.Clear();

            lock (_runStateLock)
            {
                _activeConfigRuns.Clear();
                _activeFolderRuns.Clear();
            }

            _isRunning = false;
        }

        private static void CheckStartupBackups()
        {
            var now = DateTime.Now;

            foreach (var config in GetBackupConfigs())
            {
                if (config?.Automation == null || !config.Automation.AutoBackupEnabled || !config.Automation.RunOnAppStart)
                {
                    continue;
                }

                _ = Task.Run(() => QueueAutoBackupAsync(
                    config,
                    now,
                    I18n.GetString("AutoBackup_Reason_AppStart"),
                    isScheduledTrigger: false,
                    targetFolder: null,
                    updateAutomationState: true));
            }
        }

        // ── Timer lifecycle ─────────────────────────────────────────

        private static bool HasConditionalConfigs()
        {
            return GetBackupConfigs()
                .Any(c => c?.Automation?.AutoBackupEnabled == true &&
                          c.Automation.ConditionalModeEnabled &&
                          c.Automation.ConditionType == AutomationConditionType.FileUnlocked);
        }

        private static bool HasScheduledOrIntervalConfigs()
        {
            return GetBackupConfigs()
                .Any(c => c?.Automation?.AutoBackupEnabled == true &&
                          (c.Automation.ScheduledMode || c.Automation.IntervalMode));
        }

        private static void EvaluateTimers()
        {
            var needConditional = HasConditionalConfigs();
            var needScheduleOrInterval = HasScheduledOrIntervalConfigs();

            if (needConditional && !_conditionTimerEnabled)
                StartConditionTimer();
            else if (!needConditional && _conditionTimerEnabled)
                StopConditionTimer();

            if (needScheduleOrInterval && !_scheduleTimerEnabled)
                StartScheduleTimer();
            else if (!needScheduleOrInterval && _scheduleTimerEnabled)
                StopScheduleTimer();
        }

        private static void StartScheduleTimer()
        {
            _scheduleTimer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            _scheduleTimerEnabled = true;
        }

        private static void StopScheduleTimer()
        {
            _scheduleTimer?.Dispose();
            _scheduleTimer = null;
            _scheduleTimerEnabled = false;
        }

        private static void StartConditionTimer()
        {
            _conditionTimer = new Timer(OnConditionTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _conditionTimerEnabled = true;
        }

        private static void StopConditionTimer()
        {
            _conditionTimer?.Dispose();
            _conditionTimer = null;
            _conditionTimerEnabled = false;
        }

        private static void OnConfigSaved()
        {
            EvaluateTimers();
        }

        // ── Timer tick handlers ─────────────────────────────────────

        /// <summary>
        /// 调度/间隔定时器回调（每 60 秒）：先处理计划任务条目，同一配置每轮只触发
        /// 第一个命中的条目且 2 分钟内不重复触发；计划已触发则本轮跳过间隔判断。
        /// 间隔模式按"距上次自动备份的分钟数"到期触发，间隔被钳制在 1–10080 分钟。
        /// 触发均为 fire-and-forget（Task.Run），不阻塞定时器线程。
        /// </summary>
        /// <remarks>
        /// 进入即用 <c>WaitAsync(0)</c> 做非阻塞重入守卫：上一轮尚未跑完时本轮直接放弃，
        /// 而不是排队堆积。
        /// </remarks>
        private static async void OnTick(object? state)
        {
            if (!await _tickLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                var now = DateTime.Now;
                var utcNow = DateTime.UtcNow;

                foreach (var config in GetBackupConfigs())
                {
                    if (config?.Automation == null)
                    {
                        continue;
                    }

                    var automation = config.Automation;
                    automation.Normalize(config.SourceFolders);

                    if (!automation.AutoBackupEnabled)
                    {
                        continue;
                    }

                    bool scheduledTriggered = false;

                    if (automation.ScheduledMode)
                    {
                        foreach (var entry in automation.ScheduleEntries)
                        {
                            if (!entry.ShouldTriggerNow(now))
                            {
                                continue;
                            }

                            if (entry.LastTriggeredUtc != DateTime.MinValue &&
                                (utcNow - entry.LastTriggeredUtc) < TimeSpan.FromMinutes(2))
                            {
                                continue;
                            }

                            string desc = FormatScheduleDescription(entry);
                            _ = Task.Run(() => QueueAutoBackupAsync(
                                config,
                                now,
                                I18n.Format("AutoBackup_Reason_Scheduled", desc),
                                isScheduledTrigger: true,
                                targetFolder: null,
                                updateAutomationState: true));
                            entry.LastTriggeredUtc = utcNow;
                            scheduledTriggered = true;
                            break;
                        }
                    }

                    if (scheduledTriggered || !automation.IntervalMode)
                    {
                        continue;
                    }

                    var intervalMinutes = Math.Clamp(automation.IntervalMinutes, 1, 10080);
                    var lastUtc = automation.LastAutoBackupUtc;
                    var due = lastUtc == DateTime.MinValue || (utcNow - lastUtc) >= TimeSpan.FromMinutes(intervalMinutes);

                    if (!due)
                    {
                        continue;
                    }

                    _ = Task.Run(() => QueueAutoBackupAsync(
                        config,
                        now,
                        I18n.Format("AutoBackup_Reason_Interval", intervalMinutes),
                        isScheduledTrigger: false,
                        targetFolder: null,
                        updateAutomationState: true));
                }
            }
            finally
            {
                _tickLock.Release();
            }
        }

        /// <summary>
        /// 条件轮询回调（每 10 秒）：按"配置|文件夹|相对路径"三元组跟踪条件文件的状态
        /// （Missing/Locked/Unlocked），仅在状态迁移时动作（Locked→Unlocked 触发备份）；
        /// 首次观测只记录基线不触发。轮末清理不再活跃的三元组，防止配置删除后残留。
        /// 同样使用非阻塞重入守卫。
        /// </summary>
        private static async void OnConditionTick(object? state)
        {
            if (!await _conditionTickLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                var activeStateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var config in GetBackupConfigs())
                {
                    if (config?.Automation == null)
                    {
                        continue;
                    }

                    var automation = config.Automation;
                    automation.Normalize(config.SourceFolders);

                    if (!automation.AutoBackupEnabled ||
                        !automation.ConditionalModeEnabled ||
                        automation.ConditionType != AutomationConditionType.FileUnlocked)
                    {
                        continue;
                    }

                    string relativePath = NormalizeConditionRelativePath(automation.ConditionRelativePath);
                    if (string.IsNullOrWhiteSpace(relativePath))
                    {
                        continue;
                    }

                    foreach (var folder in ResolveConditionFolders(config))
                    {
                        string stateKey = BuildConditionStateKey(config.Id, folder.Path, relativePath);
                        activeStateKeys.Add(stateKey);

                        string? conditionFilePath = TryBuildConditionFilePath(folder, relativePath);
                        var currentState = EvaluateConditionFileState(conditionFilePath);

                        if (!_conditionStates.TryGetValue(stateKey, out var previousState))
                        {
                            _conditionStates[stateKey] = currentState;
                            continue;
                        }

                        if (previousState == currentState)
                        {
                            continue;
                        }

                        _conditionStates[stateKey] = currentState;

                        await HandleConditionStateChangedAsync(
                            config,
                            folder,
                            relativePath,
                            previousState,
                            currentState);
                    }
                }

                CleanupConditionStates(activeStateKeys);
            }
            finally
            {
                _conditionTickLock.Release();
            }
        }

        private static IReadOnlyList<BackupConfig> GetBackupConfigs()
        {
            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            return configs == null ? Array.Empty<BackupConfig>() : configs.ToList();
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
            DateTime nowLocal,
            string reason,
            bool isScheduledTrigger,
            ManagedFolder? targetFolder,
            bool updateAutomationState)
        {
            ManagedFolder? effectiveTargetFolder = targetFolder;

            if (effectiveTargetFolder == null && config.Automation.Scope == AutomationScope.SingleFolder)
            {
                effectiveTargetFolder = ResolveSingleTargetFolder(config);
                if (effectiveTargetFolder == null)
                {
                    LogService.Log(I18n.Format("AutoBackup_Log_SingleTargetMissing", config.Name));
                    return;
                }
            }

            if (!TryEnterRunState(config, effectiveTargetFolder))
            {
                return;
            }

            try
            {
                await RunAutoBackupAsync(
                    config,
                    nowLocal,
                    reason,
                    isScheduledTrigger,
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

        /// <summary>
        /// 条件状态迁移处理：Locked→Unlocked 触发该文件夹的自动备份
        /// （SingleFolder 作用域才更新自动化状态）；Unlocked→Locked 仅记录日志。
        /// </summary>
        private static async Task HandleConditionStateChangedAsync(
            BackupConfig config,
            ManagedFolder folder,
            string conditionRelativePath,
            ConditionFileState previousState,
            ConditionFileState currentState)
        {
            if (previousState == ConditionFileState.Locked && currentState == ConditionFileState.Unlocked)
            {
                LogService.Log(I18n.Format(
                    "AutoBackup_Log_ConditionUnlocked",
                    config.Name,
                    GetFolderDisplayName(folder),
                    conditionRelativePath));

                bool updateAutomationState = config.Automation.Scope == AutomationScope.SingleFolder;

                _ = Task.Run(() => QueueAutoBackupAsync(
                    config,
                    DateTime.Now,
                    I18n.Format("AutoBackup_Reason_FileUnlocked", conditionRelativePath),
                    isScheduledTrigger: false,
                    targetFolder: folder,
                    updateAutomationState: updateAutomationState));
                return;
            }

            if (previousState == ConditionFileState.Unlocked && currentState == ConditionFileState.Locked)
            {
                LogService.Log(I18n.Format(
                    "AutoBackup_Log_ConditionLocked",
                    config.Name,
                    GetFolderDisplayName(folder),
                    conditionRelativePath));
            }
        }

        private static void CleanupConditionStates(HashSet<string> activeKeys)
        {
            foreach (var existingKey in _conditionStates.Keys)
            {
                if (!activeKeys.Contains(existingKey))
                {
                    _conditionStates.TryRemove(existingKey, out _);
                }
            }
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

        private static ConditionFileState EvaluateConditionFileState(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return ConditionFileState.Missing;
            }

            return FileLockService.IsFileLocked(filePath)
                ? ConditionFileState.Locked
                : ConditionFileState.Unlocked;
        }

        private static string FormatScheduleDescription(ScheduleEntry entry)
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
            DateTime nowLocal,
            string reason,
            bool isScheduledTrigger,
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
                    config.Automation.LastAutoBackupUtc = DateTime.UtcNow;
                    if (isScheduledTrigger)
                    {
                    }

                    ApplyNoChangeStopPolicy(config, hadChanges);
                    ConfigService.Save();
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
