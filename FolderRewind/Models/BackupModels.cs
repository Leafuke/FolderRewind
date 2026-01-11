using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    // 基础通知类，省去每个类都写一遍 PropertyChanged
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 应用程序的总配置根对象 (对应 config.json 的根)
    /// </summary>
    public class AppConfig : ObservableObject
    {
        private GlobalSettings _globalSettings = new();
        private ObservableCollection<BackupConfig> _backupConfigs = new();

        public string Version { get; set; } = "2.0"; // 配置版本号，方便未来迁移

        public GlobalSettings GlobalSettings
        {
            get => _globalSettings;
            set => SetProperty(ref _globalSettings, value);
        }

        public ObservableCollection<BackupConfig> BackupConfigs
        {
            get => _backupConfigs;
            set => SetProperty(ref _backupConfigs, value);
        }
    }

    /// <summary>
    /// 全局设置 (对应 MineBackup [General] + 全局参数)
    /// </summary>
    public class GlobalSettings : ObservableObject
    {
        private string _language = "zh_CN";
        private int _themeIndex = 1; // 0: Dark, 1: Light, etc. (default now Light)
        private string _sevenZipPath = "7z.exe"; // 全局 7z 路径
        private bool _runOnStartup = false;
        private bool _checkForUpdates = true;
        private bool _enableFileLogging = true;
        private bool _enableDebugLogs = false;
        private int _logRetentionDays = 7;
        private int _maxLogFileSizeMb = 5;
        private bool _isNavPaneOpen = true;
        private double _startupWidth = 1200;
        private double _startupHeight = 800;
        private double _navPaneWidth = 320;
        private string _fontFamily = "Segoe UI Variable";
        private double _baseFontSize = 14;
        private string _homeSortMode = "NameAsc";
        private string _lastManagerConfigId;
        private string _lastManagerFolderPath;
        private string _lastHistoryConfigId;
        private string _lastHistoryFolderPath;

        // 插件系统设置（集中管理，避免散落在 GlobalSettings 顶层）
        private PluginHostSettings _plugins = new();

        // KnotLink 互联设置
        private bool _enableKnotLink = false;
        private string _knotLinkHost = "127.0.0.1";
        private string _knotLinkAppId = "0x00000030";
        private string _knotLinkOpenSocketId = "0x00000010";
        private string _knotLinkSignalId = "0x00000020";

        public string Language { get => _language; set => SetProperty(ref _language, value); }
        public int ThemeIndex { get => _themeIndex; set => SetProperty(ref _themeIndex, value); }
        public string SevenZipPath { get => _sevenZipPath; set => SetProperty(ref _sevenZipPath, value); }
        public bool RunOnStartup { get => _runOnStartup; set => SetProperty(ref _runOnStartup, value); }
        public bool CheckForUpdates { get => _checkForUpdates; set => SetProperty(ref _checkForUpdates, value); }
        public bool EnableFileLogging { get => _enableFileLogging; set => SetProperty(ref _enableFileLogging, value); }
        public bool EnableDebugLogs { get => _enableDebugLogs; set => SetProperty(ref _enableDebugLogs, value); }
        public int LogRetentionDays { get => _logRetentionDays; set => SetProperty(ref _logRetentionDays, value); }
        public int MaxLogFileSizeMb { get => _maxLogFileSizeMb; set => SetProperty(ref _maxLogFileSizeMb, value); }
        public bool IsNavPaneOpen { get => _isNavPaneOpen; set => SetProperty(ref _isNavPaneOpen, value); }
        public double StartupWidth { get => _startupWidth; set => SetProperty(ref _startupWidth, value); }
        public double StartupHeight { get => _startupHeight; set => SetProperty(ref _startupHeight, value); }
        public double NavPaneWidth { get => _navPaneWidth; set => SetProperty(ref _navPaneWidth, value); }
        public string FontFamily { get => _fontFamily; set => SetProperty(ref _fontFamily, value); }
        public double BaseFontSize { get => _baseFontSize; set => SetProperty(ref _baseFontSize, value); }
        public string HomeSortMode { get => _homeSortMode; set => SetProperty(ref _homeSortMode, value); }

        // 记住上次在“管理/历史”页选择的配置与文件夹，避免每次回到页面都跳到第一项
        public string LastManagerConfigId { get => _lastManagerConfigId; set => SetProperty(ref _lastManagerConfigId, value); }
        public string LastManagerFolderPath { get => _lastManagerFolderPath; set => SetProperty(ref _lastManagerFolderPath, value); }
        public string LastHistoryConfigId { get => _lastHistoryConfigId; set => SetProperty(ref _lastHistoryConfigId, value); }
        public string LastHistoryFolderPath { get => _lastHistoryFolderPath; set => SetProperty(ref _lastHistoryFolderPath, value); }

        /// <summary>
        /// 插件系统设置。
        /// </summary>
        public PluginHostSettings Plugins { get => _plugins; set => SetProperty(ref _plugins, value ?? new PluginHostSettings()); }

        // KnotLink 互联设置属性
        /// <summary>
        /// 是否启用 KnotLink 互联功能
        /// </summary>
        public bool EnableKnotLink { get => _enableKnotLink; set => SetProperty(ref _enableKnotLink, value); }

        /// <summary>
        /// KnotLink 服务器地址
        /// </summary>
        public string KnotLinkHost { get => _knotLinkHost; set => SetProperty(ref _knotLinkHost, value); }

        /// <summary>
        /// KnotLink 应用标识符
        /// </summary>
        public string KnotLinkAppId { get => _knotLinkAppId; set => SetProperty(ref _knotLinkAppId, value); }

        /// <summary>
        /// KnotLink OpenSocket ID（用于命令响应）
        /// </summary>
        public string KnotLinkOpenSocketId { get => _knotLinkOpenSocketId; set => SetProperty(ref _knotLinkOpenSocketId, value); }

        /// <summary>
        /// KnotLink 信号 ID（用于事件广播）
        /// </summary>
        public string KnotLinkSignalId { get => _knotLinkSignalId; set => SetProperty(ref _knotLinkSignalId, value); }
    }

    /// <summary>
    /// 单个备份配置/任务组 (融合了 MineBackup 的 Config 和 SpecialConfig)
    /// </summary>
    public class BackupConfig : ObservableObject
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name = "新配置";
        private string _destinationPath = "";
        private string _iconGlyph = "\uE8B7"; // 默认文件夹图标
        private string _summaryText = "暂无状态";

        // 核心路径
        public string Id { get => _id; set => SetProperty(ref _id, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string DestinationPath { get => _destinationPath; set => SetProperty(ref _destinationPath, value); }

        // UI 显示用
        public string IconGlyph { get => _iconGlyph; set => SetProperty(ref _iconGlyph, value); }
        [JsonIgnore] // 不需要保存到文件，运行时生成
        public string SummaryText { get => _summaryText; set => SetProperty(ref _summaryText, value); }

        // 源文件夹列表 (替代原有的 RootPath + 扫描逻辑)
        public ObservableCollection<ManagedFolder> SourceFolders { get; set; } = new();

        // 归档设置
        public ArchiveSettings Archive { get; set; } = new();

        // 自动化与计划 (替代 SpecialConfig 中的 Tasks)
        public AutomationSettings Automation { get; set; } = new();

        // 过滤器 (黑名单/白名单)
        public FilterSettings Filters { get; set; } = new();

        // 扩展属性 (用于插件，如 Minecraft 插件存储 rcon 端口等)
        public Dictionary<string, string> ExtendedProperties { get; set; } = new();
    }

    /// <summary>
    /// 被管理的源文件夹
    /// </summary>
    public class ManagedFolder : ObservableObject
    {
        private string _path;
        private string _displayName;
        private string _description;
        private string _statusText = "就绪";
        private string _lastBackupTime = "从未备份";
        private bool _isFavorite;
        private string _coverImagePath; // 对应封面图片路径

        // 核心路径
        public string Path
        {
            get => _path;
            set { SetProperty(ref _path, value); if (string.IsNullOrEmpty(_displayName)) DisplayName = System.IO.Path.GetFileName(value); }
        }

        // 为了兼容你的 XAML {x:Bind FullPath}，我们增加一个只读属性
        [JsonIgnore]
        public string FullPath => Path;

        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

        public string Description { get => _description; set => SetProperty(ref _description, value); }

        public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }

        public string LastBackupTime { get => _lastBackupTime; set => SetProperty(ref _lastBackupTime, value); }

        [JsonIgnore] // 运行时状态，不需要存Json
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        public string CoverImagePath { get => _coverImagePath; set => SetProperty(ref _coverImagePath, value); }
    }

    public enum BackupMode
    {
        Full = 0,       // 全量备份：每次生成独立完整包
        Incremental = 1,// 增量(Smart)备份：仅备份变化文件，依赖元数据
        Overwrite = 2   // 覆写备份：使用 7z update 指令更新现有包
    }

    /// <summary>
    /// 压缩与归档设置
    /// </summary>
    public class ArchiveSettings : ObservableObject
    {
        private string _format = "7z";
        private int _compressionLevel = 5;
        private string _method = "LZMA2";
        private int _keepCount = 0;

        // 修改点：将 bool SmartBackup 改为 BackupMode 枚举
        private BackupMode _mode = BackupMode.Full;
        private string _password = "";

        public string Format { get => _format; set => SetProperty(ref _format, value); }
        public int CompressionLevel { get => _compressionLevel; set => SetProperty(ref _compressionLevel, value); }
        public string Method { get => _method; set => SetProperty(ref _method, value); }
        public int KeepCount { get => _keepCount; set => SetProperty(ref _keepCount, value); }

        public BackupMode Mode { get => _mode; set => SetProperty(ref _mode, value); }

        public string Password { get => _password; set => SetProperty(ref _password, value); }
    }

    /// <summary>
    /// 自动化设置 (定时、触发器)
    /// </summary>
    public class AutomationSettings : ObservableObject
    {
        private bool _autoBackupEnabled = false;
        private int _intervalMinutes = 60; // 间隔模式
        private bool _runOnAppStart = false;

        // 计划任务模式 (简单 cron 或 指定时间点，这里简化为每日几点)
        private bool _scheduledMode = false;
        private int _scheduledHour = 3;

        public bool AutoBackupEnabled { get => _autoBackupEnabled; set => SetProperty(ref _autoBackupEnabled, value); }
        public int IntervalMinutes { get => _intervalMinutes; set => SetProperty(ref _intervalMinutes, value); }
        public bool RunOnAppStart { get => _runOnAppStart; set => SetProperty(ref _runOnAppStart, value); }

        public bool ScheduledMode { get => _scheduledMode; set => SetProperty(ref _scheduledMode, value); }
        public int ScheduledHour { get => _scheduledHour; set => SetProperty(ref _scheduledHour, value); }
    }

    /// <summary>
    /// 过滤器设置
    /// </summary>
    public class FilterSettings : ObservableObject
    {
        // 这里的黑名单是相对于 Config 的，应用于所有 SourceFolder
        private ObservableCollection<string> _blacklist = new();
        public ObservableCollection<string> Blacklist
        {
            get => _blacklist;
            set => SetProperty(ref _blacklist, value ?? new ObservableCollection<string>());
        }
        public bool UseRegex { get; set; } = false;
    }

    // 可以在这里添加 BackupTask 用于运行时 UI 显示 (BackupTasksPage 使用)
    //public class BackupTask : ObservableObject
    //{
    //    private string _folderName;
    //    private double _progress;
    //    private string _status;
    //    private string _speed;
    //    private bool _isPaused;

    //    public string FolderName { get => _folderName; set => SetProperty(ref _folderName, value); }
    //    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    //    public string Status { get => _status; set => SetProperty(ref _status, value); }
    //    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }
    //    public bool IsPaused { get => _isPaused; set => SetProperty(ref _isPaused, value); }
    //}

    public class HistoryItem : ObservableObject
    {
        // 核心字段 (需要保存)
        public string ConfigId { get; set; }        // 所属配置ID
        public string FolderPath { get; set; }      // 所属源文件夹路径 (作为唯一标识)
        public string FolderName { get; set; }      // 文件夹名 (冗余备份，防止源被删后无法识别)
        public string FileName { get; set; }        // 备份文件名 (如 [Full]...7z)
        public DateTime Timestamp { get; set; }     // 备份时间
        public string BackupType { get; set; }      // Full, Smart, Overwrite

        private string _comment;
        public string Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value);
        }

        private bool _isImportant;
        public bool IsImportant
        {
            get => _isImportant;
            set => SetProperty(ref _isImportant, value);
        }

        // --- UI 辅助属性 (不存入 JSON) ---

        [JsonIgnore]
        public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

        [JsonIgnore]
        public string DateDisplay => Timestamp.ToString("yyyy-MM-dd");

        [JsonIgnore]
        public string Message => string.IsNullOrEmpty(Comment) ? $"[{BackupType}] 备份" : $"[{BackupType}] {Comment}";

        [JsonIgnore]
        public string FileSizeDisplay { get; set; } = "-"; // 需动态获取
    }

    public class BackupTask : ObservableObject
    {
        private string _folderName;
        private double _progress; // 0 - 100
        private string _status;
        private string _speed;
        private bool _isCompleted;
        private string _log; // 实时日志片段

        public string FolderName { get => _folderName; set => SetProperty(ref _folderName, value); }
        public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        public string Speed { get => _speed; set => SetProperty(ref _speed, value); }
        public bool IsCompleted { get => _isCompleted; set => SetProperty(ref _isCompleted, value); }

        // 这里的 Log 用于给 TaskPage 显示详细信息
        public string Log { get => _log; set => SetProperty(ref _log, value); }
    }
}