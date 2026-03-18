using FolderRewind.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class AutomationService
    {
        private static Timer? _timer;
        private static bool _isRunning = false;
        // Tick 可能重叠触发（上次未执行完），用 0 超时锁直接跳过重入。
        private static readonly SemaphoreSlim _tickLock = new(1, 1);

        public static void Start()
        {
            if (_isRunning) return;

            // 60 秒轮询一次，实际是否触发由计划/间隔条件决定。
            _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            _isRunning = true;

            CheckStartupBackups();
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _isRunning = false;
        }

        private static void CheckStartupBackups()
        {
            var now = DateTime.Now;
            // 启动即触发只跑一轮，避免错过“开机后立即备份”的场景。
            foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
            {
                if (config.Automation.AutoBackupEnabled && config.Automation.RunOnAppStart)
                {
                    _ = Task.Run(() => RunAutoBackupAsync(config, now, "Auto backup (app start)"));
                }
            }
        }

        private static async void OnTick(object? state)
        {
            // 拿不到锁说明上一个 Tick 还在跑，直接跳过本轮。
            if (!await _tickLock.WaitAsync(0)) return;

            try
            {
                var now = DateTime.Now;
                var utcNow = DateTime.UtcNow;

                foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
                {
                    if (config?.Automation == null) continue;
                    if (!config.Automation.AutoBackupEnabled) continue;

                    if (config.Automation.ScheduledMode)
                    {
                        if (config.Automation.ScheduleEntries != null && config.Automation.ScheduleEntries.Count > 0)
                        {
                            foreach (var entry in config.Automation.ScheduleEntries)
                            {
                                if (entry.ShouldTriggerNow(now))
                                {
                                    // 去重窗口：避免分钟级定时在边界抖动时重复触发。
                                    if (entry.LastTriggeredUtc != DateTime.MinValue &&
                                        (utcNow - entry.LastTriggeredUtc) < TimeSpan.FromMinutes(2))
                                        continue;

                                    string desc = FormatScheduleDescription(entry);
                                    _ = Task.Run(() => RunAutoBackupAsync(config, now, $"Scheduled backup ({desc})"));
                                    entry.LastTriggeredUtc = utcNow;
                                    break; // one backup per config per tick
                                }
                            }
                        }
                        else
                        {
                            // 兼容旧配置：没有 ScheduleEntries 时沿用旧的 ScheduledHour 逻辑。
                            if (now.Hour == config.Automation.ScheduledHour && now.Minute < 5)
                            {
                                if (!IsRunToday(config, now))
                                {
                                    _ = Task.Run(() => RunAutoBackupAsync(config, now, "Auto backup (scheduled legacy)"));
                                }
                            }
                        }
                        continue;
                    }

                    // 间隔模式
                    var intervalMinutes = Math.Clamp(config.Automation.IntervalMinutes, 1, 10080);
                    var lastUtc = config.Automation.LastAutoBackupUtc;
                    // 间隔计算统一用 UTC，避免夏令时/时区切换导致误触发。
                    var due = lastUtc == DateTime.MinValue || (utcNow - lastUtc) >= TimeSpan.FromMinutes(intervalMinutes);

                    if (due)
                    {
                        _ = Task.Run(() => RunAutoBackupAsync(config, now, $"Auto backup (interval {intervalMinutes} min)"));
                    }
                }
            }
            finally
            {
                _tickLock.Release();
            }
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

        private static async Task RunAutoBackupAsync(BackupConfig config, DateTime nowLocal, string reason)
        {
            try
            {
                LogService.Log($"[AutoBackup] {reason}: {config.Name}");
            }
            catch
            {
            }

            try
            {
                bool hadChanges = await BackupService.BackupConfigAsync(config);

                // 这里记录的是“任务执行时间”，不是“是否产生新归档”。
                config.Automation.LastAutoBackupUtc = DateTime.UtcNow;
                if (config.Automation.ScheduledMode)
                {
                    config.Automation.LastScheduledRunDateLocal = nowLocal.Date;
                }

                // 连续无变更自动停止逻辑
                if (config.Automation.StopAfterNoChangeEnabled)
                {
                    if (!hadChanges)
                    {
                        config.Automation.ConsecutiveNoChangeCount++;
                        // 达到阈值则自动停止，但是检测到level.dat被占用就暂时不停止，这是给MC存档的特殊处理，可能是玩家正在玩游戏但按了暂停，这时候他们应该不希望停止。
                        if (config.Automation.ConsecutiveNoChangeCount >= config.Automation.StopAfterNoChangeCount)
                        {
                            foreach (var folder in config.SourceFolders)
                            {
                                if(FileLockService.IsFileLocked(Path.Combine(folder.FullPath, "level.dat"))) {
                                    LogService.Log($"[AutoBackup] Detected level.dat is locked, skipping auto backup stop for '{config.Name}'");
                                    return;
                                }
                            }
                            config.Automation.AutoBackupEnabled = false;
                            LogService.Log($"[AutoBackup] Auto backup disabled for '{config.Name}': no changes detected {config.Automation.ConsecutiveNoChangeCount} consecutive times.");
                            try
                            {
                                NotificationService.ShowImportant(
                                    I18n.Format("AutoBackup_StoppedNoChanges", config.Name, config.Automation.ConsecutiveNoChangeCount.ToString()));
                            }
                            catch { }
                            config.Automation.ConsecutiveNoChangeCount = 0;
                        }
                    }
                    else
                    {
                        config.Automation.ConsecutiveNoChangeCount = 0;
                    }
                }

                ConfigService.Save();
            }
            catch (Exception ex)
            {
                try
                {
                    LogService.Log($"[AutoBackup] Failed: {config.Name}: {ex.Message}");
                }
                catch
                {
                }
            }
        }
    }
}