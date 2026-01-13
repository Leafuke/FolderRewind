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

            // 每 60 秒检查一次
            _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            _isRunning = true;

            // 检查是否有“启动时备份”的任务
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
                // 如果开启了“启动时运行”
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

                // 遍历所有配置
                foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
                {
                    if (config?.Automation == null) continue;
                    if (!config.Automation.AutoBackupEnabled) continue;

                    // 每日定时
                    if (config.Automation.ScheduledMode)
                    {
                        // 5分钟内触发一次，“今天是否已跑过”
                        if (now.Hour == config.Automation.ScheduledHour && now.Minute < 5)
                        {
                            if (!IsRunToday(config, now))
                            {
                                await RunAutoBackupAsync(config, now, "Auto backup (scheduled)");
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
                await BackupService.BackupConfigAsync(config);

                // 成功后更新时间戳并持久化
                config.Automation.LastAutoBackupUtc = DateTime.UtcNow;
                if (config.Automation.ScheduledMode)
                {
                    config.Automation.LastScheduledRunDateLocal = nowLocal.Date;
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