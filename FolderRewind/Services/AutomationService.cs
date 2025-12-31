using FolderRewind.Models;
using System;
using System.Collections.Generic;
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

        // 上次检查时间，防止重复触发
        private static DateTime _lastCheckTime = DateTime.MinValue;

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
                if (config.Automation.RunOnAppStart)
                {
                    await BackupService.BackupConfigAsync(config);
                    // 可以发个通知：启动备份已完成
                }
            }
        }

        private static async void OnTick(object state)
        {
            if (!await _tickLock.WaitAsync(0)) return;

            try
            {
                var now = DateTime.Now;

                // 遍历所有配置
                foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
                {
                    // 1. 间隔模式 (每隔 X 分钟)
                    // 这需要记录上次备份时间，我们在 ManagedFolder 里记录了 LastBackupTime (string)
                    // 但那是给用户看的，最好在 Config 级别或者 Metadata 里存一个准确的 DateTime LastAutoBackupTime
                    // 为了简化，这里先不实现复杂的间隔逻辑，只实现“每日定时”

                    // 2. 每日定时 (Scheduled Mode)
                    if (config.Automation.ScheduledMode && config.Automation.AutoBackupEnabled)
                    {
                        // 检查当前小时是否匹配，且今天还没备份过
                        // 这里简化逻辑：如果当前时间 > 设定时间，且上次自动备份时间不是今天
                        if (now.Hour == config.Automation.ScheduledHour && now.Minute < 5) // 5分钟窗口
                        {
                            // 检查是否今天已运行 (需要扩展 Config 模型存储 LastRunDate)
                            if (!IsRunToday(config))
                            {
                                await BackupService.BackupConfigAsync(config);
                                MarkRunToday(config);
                            }
                        }
                    }
                }
            }
            finally
            {
                _tickLock.Release();
            }
        }

        // 简单的内存标记，实际应该存入 Config
        private static Dictionary<string, DateTime> _lastRunMap = new();

        private static bool IsRunToday(BackupConfig config)
        {
            if (_lastRunMap.TryGetValue(config.Id, out var lastRun))
            {
                return lastRun.Date == DateTime.Today;
            }
            return false;
        }

        private static void MarkRunToday(BackupConfig config)
        {
            _lastRunMap[config.Id] = DateTime.Now;
        }
    }
}