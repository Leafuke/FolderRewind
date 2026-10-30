using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{

    /// <summary>
    /// Schedule entry: supports Month/Day/Hour/Minute granularity.
    /// MonthSelection: 0 = every month (wildcard), 1-12 = specific month.
    /// DaySelection:   0 = every day (wildcard), 1-31 = specific day.
    /// When DaySelection=0, MonthSelection is ignored (runs daily).
    /// </summary>
    public class ScheduleEntry : ObservableObject
    {
        private int _monthSelection = 0;
        private int _daySelection = 0;
        private int _hour = 8;
        private int _minute = 0;
        private DateTime _lastTriggeredUtc = DateTime.MinValue;

        public int MonthSelection { get => _monthSelection; set { if (SetProperty(ref _monthSelection, value)) OnPropertyChanged(nameof(NextRunDisplay)); } }
        public int DaySelection { get => _daySelection; set { if (SetProperty(ref _daySelection, value)) { OnPropertyChanged(nameof(NextRunDisplay)); OnPropertyChanged(nameof(IsMonthEnabled)); } } }

        [JsonIgnore]
        public bool IsMonthEnabled => DaySelection != 0;
        public int Hour { get => _hour; set { if (SetProperty(ref _hour, value)) OnPropertyChanged(nameof(NextRunDisplay)); } }
        public int Minute { get => _minute; set { if (SetProperty(ref _minute, value)) OnPropertyChanged(nameof(NextRunDisplay)); } }
        public DateTime LastTriggeredUtc { get => _lastTriggeredUtc; set => SetProperty(ref _lastTriggeredUtc, value); }

        [JsonIgnore]
        public string NextRunDisplay
        {
            get
            {
                var next = CalculateNextRun(DateTime.Now);
                return next.HasValue ? UserDisplayFormatter.ShortDateTime(next.Value) : "-";
            }
        }

        public DateTime? CalculateNextRun(DateTime now)
        {
            try
            {
                if (DaySelection == 0)
                {
                    var today = now.Date.AddHours(Hour).AddMinutes(Minute);
                    return today > now ? today : today.AddDays(1);
                }
                else if (MonthSelection == 0)
                {
                    int day = DaySelection;
                    var candidate = TryBuildDate(now.Year, now.Month, day, Hour, Minute);
                    if (candidate.HasValue && candidate.Value > now) return candidate;
                    for (int i = 1; i <= 12; i++)
                    {
                        var nextMonth = now.AddMonths(i);
                        candidate = TryBuildDate(nextMonth.Year, nextMonth.Month, day, Hour, Minute);
                        if (candidate.HasValue) return candidate;
                    }
                    return null;
                }
                else
                {
                    var candidate = TryBuildDate(now.Year, MonthSelection, DaySelection, Hour, Minute);
                    if (candidate.HasValue && candidate.Value > now) return candidate;
                    candidate = TryBuildDate(now.Year + 1, MonthSelection, DaySelection, Hour, Minute);
                    return candidate;
                }
            }
            catch { return null; }
        }

        public bool ShouldTriggerNow(DateTime now)
        {
            if (now.Hour != Hour || now.Minute != Minute) return false;
            if (DaySelection == 0) return true;
            if (now.Day != DaySelection) return false;
            if (MonthSelection == 0) return true;
            return now.Month == MonthSelection;
        }

        private static DateTime? TryBuildDate(int year, int month, int day, int hour, int minute)
        {
            if (month < 1 || month > 12) return null;
            int maxDay = DateTime.DaysInMonth(year, month);
            int actualDay = Math.Min(day, maxDay);
            if (actualDay < 1) return null;
            return new DateTime(year, month, actualDay, hour, minute, 0);
        }
    }

    /// <summary>
    /// 自动化作用范围。
    /// </summary>
    public enum AutomationScope
    {
        AllFolders = 0,
        SingleFolder = 1
    }

    /// <summary>
    /// 自动化条件类型。
    /// 首版仅支持“文件从占用变为解除占用”。
    /// </summary>
    public enum AutomationConditionType
    {
        FileUnlocked = 0
    }

    public class AutomationSettings : ObservableObject
    {
        private bool _autoBackupEnabled = false;
        private bool _intervalMode = true;
        private int _intervalMinutes = 60;
        private bool _runOnAppStart = false;
        private bool _scheduledMode = false;
        private AutomationScope _scope = AutomationScope.AllFolders;
        private string _targetFolderPath = string.Empty;
        private bool _conditionalModeEnabled = false;
        private AutomationConditionType _conditionType = AutomationConditionType.FileUnlocked;
        private string _conditionRelativePath = string.Empty;
        private ObservableCollection<ScheduleEntry> _scheduleEntries = new();
        private DateTime _lastAutoBackupUtc = DateTime.MinValue;

        // 连续无变更自动停止
        private bool _stopAfterNoChangeEnabled = false;
        private int _stopAfterNoChangeCount = 3;
        private int _consecutiveNoChangeCount = 0;

        public bool AutoBackupEnabled { get => _autoBackupEnabled; set => SetProperty(ref _autoBackupEnabled, value); }
        public bool IntervalMode { get => _intervalMode; set => SetProperty(ref _intervalMode, value); }
        public int IntervalMinutes { get => _intervalMinutes; set => SetProperty(ref _intervalMinutes, value); }
        public bool RunOnAppStart { get => _runOnAppStart; set => SetProperty(ref _runOnAppStart, value); }
        public bool ScheduledMode { get => _scheduledMode; set => SetProperty(ref _scheduledMode, value); }

        /// <summary>
        /// 自动化作用范围：作用于当前配置的全部文件夹，或仅作用于某个单独文件夹。
        /// </summary>
        public AutomationScope Scope { get => _scope; set => SetProperty(ref _scope, value); }

        /// <summary>
        /// 当 Scope=SingleFolder 时记录目标文件夹路径。
        /// 这里直接保存 ManagedFolder.Path，避免引入额外 ID 结构破坏兼容性。
        /// </summary>
        public string TargetFolderPath { get => _targetFolderPath; set => SetProperty(ref _targetFolderPath, value ?? string.Empty); }

        /// <summary>
        /// 是否启用条件备份模式。
        /// </summary>
        public bool ConditionalModeEnabled { get => _conditionalModeEnabled; set => SetProperty(ref _conditionalModeEnabled, value); }

        /// <summary>
        /// 条件类型。
        /// 当前仅支持文件从占用状态变为解除占用状态。
        /// </summary>
        public AutomationConditionType ConditionType { get => _conditionType; set => SetProperty(ref _conditionType, value); }

        /// <summary>
        /// 条件文件相对路径。
        /// 首版统一按源文件夹相对路径解释，例如 level.dat。
        /// </summary>
        public string ConditionRelativePath { get => _conditionRelativePath; set => SetProperty(ref _conditionRelativePath, value ?? string.Empty); }

        public ObservableCollection<ScheduleEntry> ScheduleEntries
        {
            get => _scheduleEntries;
            set => SetProperty(ref _scheduleEntries, value ?? new ObservableCollection<ScheduleEntry>());
        }

        public DateTime LastAutoBackupUtc { get => _lastAutoBackupUtc; set => SetProperty(ref _lastAutoBackupUtc, value); }

        /// <summary>
        /// 是否启用“连续无变更自动停止”功能。
        /// </summary>
        public bool StopAfterNoChangeEnabled { get => _stopAfterNoChangeEnabled; set => SetProperty(ref _stopAfterNoChangeEnabled, value); }

        /// <summary>
        /// 连续多少次未发现更改后自动停止自动备份任务。
        /// </summary>
        public int StopAfterNoChangeCount { get => _stopAfterNoChangeCount; set => SetProperty(ref _stopAfterNoChangeCount, value); }
        public int ConsecutiveNoChangeCount { get => _consecutiveNoChangeCount; set => SetProperty(ref _consecutiveNoChangeCount, value); }

        /// <summary>
        /// 补齐新增字段默认值，并在需要时校正单文件夹目标。
        /// </summary>
        public void Normalize(IEnumerable<ManagedFolder>? sourceFolders = null)
        {
            ScheduleEntries ??= new ObservableCollection<ScheduleEntry>();
            TargetFolderPath ??= string.Empty;
            ConditionRelativePath ??= string.Empty;

            if (sourceFolders == null)
            {
                return;
            }

            var folders = sourceFolders
                .Where(folder => folder != null && !string.IsNullOrWhiteSpace(folder.Path))
                .ToList();

            if (Scope != AutomationScope.SingleFolder)
            {
                return;
            }

            if (folders.Count == 0)
            {
                TargetFolderPath = string.Empty;
                return;
            }

            bool hasMatchedFolder = folders.Any(folder =>
                string.Equals(folder.Path, TargetFolderPath, StringComparison.OrdinalIgnoreCase));

            if (!hasMatchedFolder)
            {
                // 旧配置没有单文件夹目标，或目标文件夹已被移除时，自动回退到首个可用文件夹。
                TargetFolderPath = folders[0].Path;
            }
        }
    }
}
