using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class AutomationService
    {
        private static Timer _timer;
        private static bool _isRunning = false;
        private static readonly SemaphoreSlim _tickLock = new(1, 1);

        public static void Start()
        {
            if (_isRunning) return;

            _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            _isRunning = true;

            CheckStartupBackups();
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _isRunning = false;
        }

        private static async void CheckStartupBackups()
        {
            var now = DateTime.Now;
            foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
            {
                if (config.Automation.AutoBackupEnabled && config.Automation.RunOnAppStart)
                {
                    await RunAutoBackupAsync(config, now, "Auto backup (app start)");
                }
            }
        }

        private static async void OnTick(object state)
        {
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
                                    // Dedup: skip if triggered within last 2 minutes
                                    if (entry.LastTriggeredUtc != DateTime.MinValue &&
                                        (utcNow - entry.LastTriggeredUtc) < TimeSpan.FromMinutes(2))
                                        continue;

                                    string desc = FormatScheduleDescription(entry);
                                    await RunAutoBackupAsync(config, now, $"Scheduled backup ({desc})");
                                    entry.LastTriggeredUtc = utcNow;
                                    break; // one backup per config per tick
                                }
                            }
                        }
                        else
                        {
                            // Legacy fallback: old ScheduledHour only
                            if (now.Hour == config.Automation.ScheduledHour && now.Minute < 5)
                            {
                                if (!IsRunToday(config, now))
                                {
                                    await RunAutoBackupAsync(config, now, "Auto backup (scheduled legacy)");
                                }
                            }
                        }
                        continue;
                    }

                    // 间隔模式
                    var intervalMinutes = Math.Clamp(config.Automation.IntervalMinutes, 1, 10080);
                    var lastUtc = config.Automation.LastAutoBackupUtc;
                    var due = lastUtc == DateTime.MinValue || (utcNow - lastUtc) >= TimeSpan.FromMinutes(intervalMinutes);

                    if (due)
                    {
                        await RunAutoBackupAsync(config, now, $"Auto backup (interval {intervalMinutes} min)");
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

                // 成功后更新时间戳并持久化
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
                        if (config.Automation.ConsecutiveNoChangeCount >= config.Automation.StopAfterNoChangeCount)
                        {
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