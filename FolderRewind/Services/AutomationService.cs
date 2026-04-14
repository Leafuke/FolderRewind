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
    public static class AutomationService
    {
        private static Timer? _scheduleTimer;
        private static Timer? _conditionTimer;
        private static bool _isRunning;

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

            _scheduleTimer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            _conditionTimer = new Timer(OnConditionTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _isRunning = true;

            CheckStartupBackups();
        }

        public static void Stop()
        {
            _scheduleTimer?.Dispose();
            _scheduleTimer = null;

            _conditionTimer?.Dispose();
            _conditionTimer = null;

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
                        if (automation.ScheduleEntries != null && automation.ScheduleEntries.Count > 0)
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
                        else if (now.Hour == automation.ScheduledHour && now.Minute < 5)
                        {
                            if (!IsRunToday(config, now))
                            {
                                _ = Task.Run(() => QueueAutoBackupAsync(
                                    config,
                                    now,
                                    I18n.GetString("AutoBackup_Reason_ScheduledLegacy"),
                                    isScheduledTrigger: true,
                                    targetFolder: null,
                                    updateAutomationState: true));
                                scheduledTriggered = true;
                            }
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

        private static string? TryBuildConditionFilePath(ManagedFolder folder, string relativePath)
        {
            if (folder == null || string.IsNullOrWhiteSpace(folder.FullPath) || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            if (Path.IsPathRooted(relativePath))
            {
                return null;
            }

            try
            {
                string folderRoot = Path.GetFullPath(folder.FullPath);
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

        private static bool IsRunToday(BackupConfig config, DateTime now)
        {
            try
            {
                var last = config.Automation.LastScheduledRunDateLocal;
                return last != DateTime.MinValue && last.Date == now.Date;
            }
            catch
            {
                return false;
            }
        }

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
                    ? await BackupService.BackupConfigAsync(config)
                    : await BackupService.BackupFolderAsync(config, targetFolder);

                if (updateAutomationState)
                {
                    config.Automation.LastAutoBackupUtc = DateTime.UtcNow;
                    if (isScheduledTrigger)
                    {
                        config.Automation.LastScheduledRunDateLocal = nowLocal.Date;
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
                if (FileLockService.IsFileLocked(Path.Combine(folder.FullPath, "level.dat")))
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

            if (!string.IsNullOrWhiteSpace(folder.FullPath))
            {
                return Path.GetFileName(folder.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return string.Empty;
        }
    }
}
