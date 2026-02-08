using FolderRewind.Services;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    public enum CloseBehavior
    {
        Ask = 0,
        MinimizeToTray = 1,
        Exit = 2
    }

    // 基础通知类，省去每个类都写一遍 PropertyChanged
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string propertyName = null)
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
        private int _themeIndex = 1; // 0: Dark, 1: Light
        private string _sevenZipPath = "7za.exe"; // 全局 7z 路径（内置 7za.exe）
        private bool _runOnStartup = false;
        private bool _enableFileLogging = true;
        private int _logRetentionDays = 7;
        private int _maxLogFileSizeMb = 5;
        private bool _isNavPaneOpen = true;
        private double _startupWidth = 1200;
        private double _startupHeight = 800;
        private double _navPaneWidth = 180;
        private string _fontFamily = "";
        private double _baseFontSize = 14;
        private string _homeSortMode = "NameAsc";
        private string _lastManagerConfigId;
        private string _lastManagerFolderPath;
        private string _lastHistoryConfigId;
        private string _lastHistoryFolderPath;

        private bool _useHistoryStatusColors = true;

        // 关闭行为
        private CloseBehavior _closeBehavior = CloseBehavior.Ask;
        private bool _rememberCloseBehavior = false;

        // 插件系统设置（集中管理，避免散落在 GlobalSettings 顶层）
        private PluginHostSettings _plugins = new();

        // KnotLink 互联设置
        private bool _enableKnotLink = false;
        private string _knotLinkHost = "127.0.0.1";
        private string _knotLinkAppId = "0x00000020";
        private string _knotLinkOpenSocketId = "0x00000010";
        private string _knotLinkSignalId = "0x00000020";

        // 快捷键/热键
        private HotkeySettings _hotkeys = new();

        // 通知设置
        private bool _enableNotifications = true;

        // 警告设置
        private int _fileSizeWarningThresholdKB = 5; // 备份文件小于此值(KB)时触发警告

        // 公告系统
        private bool _enableNotices = true;
        private string _noticeLastSeenVersion = "";

        public string Language { get => _language; set => SetProperty(ref _language, value); }
        public int ThemeIndex { get => _themeIndex; set => SetProperty(ref _themeIndex, value); }
        public string SevenZipPath { get => _sevenZipPath; set => SetProperty(ref _sevenZipPath, value); }
        public bool RunOnStartup { get => _runOnStartup; set => SetProperty(ref _runOnStartup, value); }
        public bool EnableFileLogging { get => _enableFileLogging; set => SetProperty(ref _enableFileLogging, value); }
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
        /// 是否在历史记录页使用彩色节点区分状态
        /// </summary>
        public bool UseHistoryStatusColors { get => _useHistoryStatusColors; set => SetProperty(ref _useHistoryStatusColors, value); }

        /// <summary>
        /// 点击窗口关闭按钮时的默认行为。
        /// </summary>
        public CloseBehavior CloseBehavior { get => _closeBehavior; set => SetProperty(ref _closeBehavior, value); }

        /// <summary>
        /// 是否记住关闭按钮行为并不再询问。
        /// </summary>
        public bool RememberCloseBehavior { get => _rememberCloseBehavior; set => SetProperty(ref _rememberCloseBehavior, value); }

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

        /// <summary>
        /// 快捷键/热键绑定（允许用户修改）。
        /// </summary>
        public HotkeySettings Hotkeys { get => _hotkeys; set => SetProperty(ref _hotkeys, value ?? new HotkeySettings()); }

        /// <summary>
        /// 是否启用应用内/系统通知（全局开关）。
        /// </summary>
        public bool EnableNotifications { get => _enableNotifications; set => SetProperty(ref _enableNotifications, value); }

        /// <summary>
        /// 备份文件大小警告阈值(KB)。备份生成的文件小于此大小时触发 AppNotification 警告。
        /// 参考 MineBackup 的文件大小检查逻辑。默认 5KB。
        /// </summary>
        public int FileSizeWarningThresholdKB { get => _fileSizeWarningThresholdKB; set => SetProperty(ref _fileSizeWarningThresholdKB, value); }

        /// <summary>
        /// 是否接收公告通知。
        /// </summary>
        public bool EnableNotices { get => _enableNotices; set => SetProperty(ref _enableNotices, value); }

        /// <summary>
        /// 上次已读的公告版本标识（Last-Modified 或内容 hash），用于检测是否有新公告。
        /// </summary>
        public string NoticeLastSeenVersion { get => _noticeLastSeenVersion; set => SetProperty(ref _noticeLastSeenVersion, value); }
    }

    /// <summary>
    /// 单个备份配置/任务组 (融合了 MineBackup 的 Config 和 SpecialConfig)
    /// </summary>
    public class BackupConfig : ObservableObject
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name = I18n.Format("Config_DefaultBackupName");
        private string _destinationPath = "";
        private string _iconGlyph = "\uE8B7"; // 默认文件夹图标
        private string _summaryText = I18n.Format("BackupConfig_DefaultSummary");
        private string _configType = "Default"; // 配置类型，由插件定义，如 "Minecraft Saves"

        // 核心路径
        public string Id { get => _id; set => SetProperty(ref _id, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string DestinationPath { get => _destinationPath; set => SetProperty(ref _destinationPath, value); }

        /// <summary>
        /// 配置类型。默认为 "Default"。
        /// 插件可以定义自己的配置类型，如 "Minecraft Saves"。
        /// </summary>
        public string ConfigType { get => _configType; set => SetProperty(ref _configType, value ?? "Default"); }

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
        private string _statusText = I18n.Format("FolderManager_Status");
        private string _lastBackupTime = I18n.Format("FolderManager_NeverBackedUp");
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
        private BackupMode _mode = BackupMode.Full;
        private string _password = "";
        private bool _skipIfUnchanged = true;      // 无变更时跳过备份
        private int _cpuThreads = 0;               // CPU 线程数, 0 = 自动
        private bool _backupBeforeRestore = false;  // 还原前先执行一次备份
        private int _maxSmartBackupsPerFull = 0;    // 智能备份链长度限制，0 = 不限制
        private bool _safeDeleteEnabled = true;     // 安全删除：删除增量备份时自动合并内容到下一个备份

        public string Format { get => _format; set => SetProperty(ref _format, value); }
        public int CompressionLevel { get => _compressionLevel; set => SetProperty(ref _compressionLevel, value); }
        public string Method { get => _method; set => SetProperty(ref _method, value); }
        public int KeepCount { get => _keepCount; set => SetProperty(ref _keepCount, value); }
        public BackupMode Mode { get => _mode; set => SetProperty(ref _mode, value); }
        public string Password { get => _password; set => SetProperty(ref _password, value); }

        /// <summary>
        /// 无变更时跳过备份，即使是全量模式也会先检测文件变化
        /// </summary>
        public bool SkipIfUnchanged { get => _skipIfUnchanged; set => SetProperty(ref _skipIfUnchanged, value); }

        /// <summary>
        /// CPU 线程数，0 表示自动（由 7z 决定），传递给 7z 的 -mmt 参数
        /// </summary>
        public int CpuThreads { get => _cpuThreads; set => SetProperty(ref _cpuThreads, value); }

        /// <summary>
        /// 还原前先自动执行一次备份，防止误操作丢失当前数据
        /// </summary>
        public bool BackupBeforeRestore { get => _backupBeforeRestore; set => SetProperty(ref _backupBeforeRestore, value); }

        /// <summary>
        /// 智能备份链长度限制：当连续增量备份达到此数量时，强制执行一次全量备份以截断链条。
        /// 0 表示不限制（增量链可以无限延长）。
        /// 参考 MineBackup 的 maxSmartBackupsPerFull 逻辑。
        /// </summary>
        public int MaxSmartBackupsPerFull { get => _maxSmartBackupsPerFull; set => SetProperty(ref _maxSmartBackupsPerFull, value); }

        /// <summary>
        /// 安全删除模式：在自动清理旧备份或手动删除时，
        /// 如果被删文件是增量链的一部分，会先将其内容合并到下一个备份中，再执行删除。
        /// 这样可以避免增量链断裂导致还原失败。
        /// 参考 MineBackup 的 DoSafeDeleteBackup 逻辑。
        /// </summary>
        public bool SafeDeleteEnabled { get => _safeDeleteEnabled; set => SetProperty(ref _safeDeleteEnabled, value); }
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

        // 用于去重/防止重复触发：持久化记录上次自动备份时间
        private DateTime _lastAutoBackupUtc = DateTime.MinValue;
        private DateTime _lastScheduledRunDateLocal = DateTime.MinValue;

        public bool AutoBackupEnabled { get => _autoBackupEnabled; set => SetProperty(ref _autoBackupEnabled, value); }
        public int IntervalMinutes { get => _intervalMinutes; set => SetProperty(ref _intervalMinutes, value); }
        public bool RunOnAppStart { get => _runOnAppStart; set => SetProperty(ref _runOnAppStart, value); }

        public bool ScheduledMode { get => _scheduledMode; set => SetProperty(ref _scheduledMode, value); }
        public int ScheduledHour { get => _scheduledHour; set => SetProperty(ref _scheduledHour, value); }

        public DateTime LastAutoBackupUtc { get => _lastAutoBackupUtc; set => SetProperty(ref _lastAutoBackupUtc, value); }

        // 仅用于“每日定时”去重：记录上次成功运行的日期（本地日期）
        public DateTime LastScheduledRunDateLocal { get => _lastScheduledRunDateLocal; set => SetProperty(ref _lastScheduledRunDateLocal, value); }
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

        /// <summary>
        /// 还原白名单：Clean 还原时不会清除的文件/文件夹
        /// </summary>
        private ObservableCollection<string> _restoreWhitelist = new();
        public ObservableCollection<string> RestoreWhitelist
        {
            get => _restoreWhitelist;
            set => SetProperty(ref _restoreWhitelist, value ?? new ObservableCollection<string>());
        }
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
        public string Message => string.IsNullOrEmpty(Comment)
            ? I18n.Format("HistoryItem_Message_NoComment", BackupType)
            : I18n.Format("HistoryItem_Message_WithComment", BackupType, Comment);

        [JsonIgnore]
        public string FileSizeDisplay { get; set; } = "-"; // 需动态获取

        private bool _isMissing;
        /// <summary>
        /// 运行时状态：备份文件是否缺失
        /// </summary>
        [JsonIgnore]
        public bool IsMissing { get => _isMissing; set => SetProperty(ref _isMissing, value); }

        private Brush? _timelineLineBrush;
        private Brush? _timelineNodeFillBrush;
        private Brush? _timelineNodeBorderBrush;

        [JsonIgnore]
        public Brush? TimelineLineBrush { get => _timelineLineBrush; set => SetProperty(ref _timelineLineBrush, value); }

        [JsonIgnore]
        public Brush? TimelineNodeFillBrush { get => _timelineNodeFillBrush; set => SetProperty(ref _timelineNodeFillBrush, value); }

        [JsonIgnore]
        public Brush? TimelineNodeBorderBrush { get => _timelineNodeBorderBrush; set => SetProperty(ref _timelineNodeBorderBrush, value); }
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