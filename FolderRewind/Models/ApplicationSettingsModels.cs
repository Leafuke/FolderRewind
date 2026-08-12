using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    public enum HistoryViewMode
    {
        PerSource = 0,
        ByRun = 1
    }


    public enum CloseBehavior
    {
        Ask = 0,
        MinimizeToTray = 1,
        Exit = 2
    }

    // 基础通知类，省去每个类都写一遍 PropertyChanged
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonExtensionData]
        public Dictionary<string, JsonElement> SchemaExtensions { get; set; } = new();

        public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
        private int _schemaVersion = 1;
        private GlobalSettings _globalSettings = new();
        private ObservableCollection<BackupConfig> _backupConfigs = new();
        private ObservableCollection<BackupPreset> _backupPresets = new();

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion
        {
            get => _schemaVersion;
            set => SetProperty(ref _schemaVersion, value);
        }

        public GlobalSettings GlobalSettings
        {
            get => _globalSettings;
            set => SetProperty(ref _globalSettings, value ?? new GlobalSettings());
        }

        public ObservableCollection<BackupConfig> BackupConfigs
        {
            get => _backupConfigs;
            set => SetProperty(ref _backupConfigs, value ?? new ObservableCollection<BackupConfig>());
        }

        [JsonPropertyName("Templates")]
        public ObservableCollection<BackupPreset> BackupPresets
        {
            get => _backupPresets;
            set => SetProperty(ref _backupPresets, value ?? new ObservableCollection<BackupPreset>());
        }
    }

    /// <summary>
    /// 全局设置 (对应 MineBackup [General] + 全局参数)
    /// </summary>
    public class GlobalSettings : ObservableObject
    {
        private string _language = "system";
        private int _themeIndex = 1; // 0: Dark, 1: Light, 2: System
        private string _sevenZipPath = "7za.exe"; // 全局 7z 路径（内置 7za.exe）
        private string _rcloneExecutablePath = "";
        private string _defaultCloudRemoteBasePath = "remote:FolderRewind";
        private string _defaultBackupRootPath = "";
        private bool _autoDownloadMissingCloudBackupsBeforeRestore = true;
        private bool _runOnStartup = false;
        private bool _silentStartup = false;
        private bool _enableFileLogging = true;
        private int _logRetentionDays = 7;
        private int _maxLogFileSizeMb = 5;
        private bool _isNavPaneOpen = true;
        private double _startupWidth = 1300;
        private double _startupHeight = 900;
        private double _navPaneWidth = 180;
        private string _fontFamily = "";
        private double _baseFontSize = 14;
        private string _homeSortMode = "NameAsc";
        private string _lastManagerConfigId = "";
        private string _lastManagerFolderPath = "";
        private string _lastHistoryConfigId = "";
        private string _lastHistoryFolderPath = "";
        private HistoryViewMode _lastHistoryViewMode = HistoryViewMode.PerSource;
        private int _sponsorAccentColorIndex = 0;
        private int _sponsorBackdropIndex = 0;
        private string _sponsorTitleText = "";
        private string _sponsorTitleIconGlyph = IconCatalog.DefaultConfigIconGlyph;
        private bool _sponsorBackgroundEnabled = false;
        private string _sponsorBackgroundImagePath = "";
        private int _sponsorBackgroundStretchIndex = 0;
        private double _sponsorBackgroundImageOpacity = 0.28;
        private double _sponsorBackgroundOverlayOpacity = 0.62;
        private int _completionSoundIndex = 0;
        private string _completionSoundCustomPath = "";
        private bool _sponsorEntitlementCached = false;
        private DateTime _sponsorEntitlementLastVerifiedUtc = DateTime.MinValue;

        private bool _useHistoryStatusColors = true;

        // 关闭行为
        private CloseBehavior _closeBehavior = CloseBehavior.Ask;
        private bool _rememberCloseBehavior = false;

        // 插件系统设置（集中管理，避免散落在 GlobalSettings 顶层）
        private PluginHostSettings _plugins = new();
        private GameDiscoverySettings _gameDiscovery = new();

        // KnotLink 互联设置
        private bool _enableKnotLink = false;
        private string _knotLinkHost = "127.0.0.1";
        private string _knotLinkAppId = "0x00000020";
        private string _knotLinkOpenSocketId = "0x00000010";
        private string _knotLinkSignalId = "0x00000020";
        private bool _autoStartKnotLinkServer = false;

        // 快捷键/热键
        private HotkeySettings _hotkeys = new();

        // 通知设置
        private bool _enableNotifications = true;
        private int _toastNotificationLevel = 2; // 0=Off, 1=ErrorOnly, 2=ImportantAndAbove, 3=All

        // 警告设置
        private int _fileSizeWarningThresholdKB = 5; // 备份文件小于此值(KB)时触发警告

        // 公告系统
        private bool _enableNotices = true;
        private string _noticeLastSeenVersion = "";
        private bool _enableUpdateReminder = true;
        private int _appUpdatePreferredSource = 1; // 0=Official, 1=Mirror1, 2=Mirror2, 3=Custom
        private bool _appUpdateAutoFallback = true;
        private string _appUpdateCustomMirrorUrl = "";
        private string _gitHubOAuthClientId = "";

        // 首次启动引导
        private bool _hasShownFirstLaunchGuide = false;

        // 核心功能自动校验
        private bool _hasTriggeredInitialCoreValidation = false;
        private bool _lastCoreValidationPassed = false;
        private DateTime _lastCoreValidationUtc = DateTime.MinValue;
        private string _lastCoreValidationSummary = "";

        public string Language { get => _language; set => SetProperty(ref _language, value); }
        public int ThemeIndex { get => _themeIndex; set => SetProperty(ref _themeIndex, value); }
        public string SevenZipPath { get => _sevenZipPath; set => SetProperty(ref _sevenZipPath, value); }
        public string RcloneExecutablePath { get => _rcloneExecutablePath; set => SetProperty(ref _rcloneExecutablePath, value ?? string.Empty); }
        public string DefaultCloudRemoteBasePath { get => _defaultCloudRemoteBasePath; set => SetProperty(ref _defaultCloudRemoteBasePath, value ?? string.Empty); }
        public string DefaultBackupRootPath { get => _defaultBackupRootPath; set => SetProperty(ref _defaultBackupRootPath, value); }
        public bool AutoDownloadMissingCloudBackupsBeforeRestore { get => _autoDownloadMissingCloudBackupsBeforeRestore; set => SetProperty(ref _autoDownloadMissingCloudBackupsBeforeRestore, value); }
        public bool RunOnStartup { get => _runOnStartup; set => SetProperty(ref _runOnStartup, value); }
        public bool SilentStartup { get => _silentStartup; set => SetProperty(ref _silentStartup, value); }
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
        public string LastManagerConfigId { get => _lastManagerConfigId; set => SetProperty(ref _lastManagerConfigId, value ?? string.Empty); }
        public string LastManagerFolderPath { get => _lastManagerFolderPath; set => SetProperty(ref _lastManagerFolderPath, value ?? string.Empty); }
        public string LastHistoryConfigId { get => _lastHistoryConfigId; set => SetProperty(ref _lastHistoryConfigId, value ?? string.Empty); }
        public string LastHistoryFolderPath { get => _lastHistoryFolderPath; set => SetProperty(ref _lastHistoryFolderPath, value ?? string.Empty); }
        public HistoryViewMode LastHistoryViewMode { get => _lastHistoryViewMode; set => SetProperty(ref _lastHistoryViewMode, value); }

        /// <summary>
        /// 赞助版主题色预设。0 表示继续跟随系统 Accent，避免免费版被新字段影响。
        /// </summary>
        public int SponsorAccentColorIndex { get => _sponsorAccentColorIndex; set => SetProperty(ref _sponsorAccentColorIndex, value); }

        /// <summary>
        /// 赞助版窗口材质：0=Mica，1=Acrylic。
        /// </summary>
        public int SponsorBackdropIndex { get => _sponsorBackdropIndex; set => SetProperty(ref _sponsorBackdropIndex, value); }

        /// <summary>
        /// 赞助版自定义标题栏文字。留空时使用默认应用名。
        /// </summary>
        public string SponsorTitleText { get => _sponsorTitleText; set => SetProperty(ref _sponsorTitleText, value ?? string.Empty); }

        /// <summary>
        /// 赞助版自定义标题栏图标，保存 Segoe MDL2 glyph，避免引入外部图标文件兼容风险。
        /// </summary>
        public string SponsorTitleIconGlyph { get => _sponsorTitleIconGlyph; set => SetProperty(ref _sponsorTitleIconGlyph, value ?? IconCatalog.DefaultConfigIconGlyph); }

        /// <summary>
        /// 赞助版背景图片总开关。未解锁赞助版时运行时会忽略它，避免旧配置影响免费版体验。
        /// </summary>
        public bool SponsorBackgroundEnabled { get => _sponsorBackgroundEnabled; set => SetProperty(ref _sponsorBackgroundEnabled, value); }

        /// <summary>
        /// 复制到应用配置目录后的背景图片路径，不保存用户原始文件位置。
        /// </summary>
        public string SponsorBackgroundImagePath { get => _sponsorBackgroundImagePath; set => SetProperty(ref _sponsorBackgroundImagePath, value ?? string.Empty); }

        /// <summary>
        /// 背景图片显示方式：0=UniformToFill，1=Uniform，2=Fill。
        /// </summary>
        public int SponsorBackgroundStretchIndex { get => _sponsorBackgroundStretchIndex; set => SetProperty(ref _sponsorBackgroundStretchIndex, value); }

        /// <summary>
        /// 背景图片本体透明度，控制图片存在感。
        /// </summary>
        public double SponsorBackgroundImageOpacity { get => _sponsorBackgroundImageOpacity; set => SetProperty(ref _sponsorBackgroundImageOpacity, value); }

        /// <summary>
        /// 背景遮罩透明度，用于保证文字和卡片仍然清楚。
        /// </summary>
        public double SponsorBackgroundOverlayOpacity { get => _sponsorBackgroundOverlayOpacity; set => SetProperty(ref _sponsorBackgroundOverlayOpacity, value); }

        /// <summary>
        /// 备份/还原完成后的音效。0=无，1=默认音效；赞助者可用自定义文件替换默认音效。
        /// </summary>
        public int CompletionSoundIndex { get => _completionSoundIndex; set => SetProperty(ref _completionSoundIndex, value); }

        /// <summary>
        /// 复制到应用配置目录后的自定义完成音效路径。只有赞助者版本会使用它。
        /// </summary>
        public string CompletionSoundCustomPath { get => _completionSoundCustomPath; set => SetProperty(ref _completionSoundCustomPath, value ?? string.Empty); }

        /// <summary>
        /// 本机最后一次确认过的赞助授权。它只用于启动首帧恢复外观，后台 Store 刷新会继续校正。
        /// </summary>
        public bool SponsorEntitlementCached { get => _sponsorEntitlementCached; set => SetProperty(ref _sponsorEntitlementCached, value); }

        /// <summary>
        /// 最近一次从 Microsoft Store 确认授权的 UTC 时间，用于诊断“为什么本机记得我是赞助者”。
        /// </summary>
        public DateTime SponsorEntitlementLastVerifiedUtc { get => _sponsorEntitlementLastVerifiedUtc; set => SetProperty(ref _sponsorEntitlementLastVerifiedUtc, value); }

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

        public GameDiscoverySettings GameDiscovery { get => _gameDiscovery; set => SetProperty(ref _gameDiscovery, value ?? new GameDiscoverySettings()); }

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
        /// 应用启动时自动启动 KnotLink 服务端进程
        /// </summary>
        public bool AutoStartKnotLinkServer { get => _autoStartKnotLinkServer; set => SetProperty(ref _autoStartKnotLinkServer, value); }

        /// <summary>
        /// 快捷键/热键绑定（允许用户修改）。
        /// </summary>
        public HotkeySettings Hotkeys { get => _hotkeys; set => SetProperty(ref _hotkeys, value ?? new HotkeySettings()); }

        /// <summary>
        /// 是否启用应用内/系统通知（全局开关）。
        /// </summary>
        public bool EnableNotifications { get => _enableNotifications; set => SetProperty(ref _enableNotifications, value); }

        /// <summary>
        /// 系统 Toast 通知等级阈值。控制何时发送系统级弹窗通知。
        /// 0=关闭, 1=仅错误, 2=重要及以上(默认), 3=全部
        /// </summary>
        public int ToastNotificationLevel { get => _toastNotificationLevel; set => SetProperty(ref _toastNotificationLevel, value); }

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

        /// <summary>
        /// 是否在启动时检查 GitHub Release 更新提醒。
        /// </summary>
        public bool EnableUpdateReminder { get => _enableUpdateReminder; set => SetProperty(ref _enableUpdateReminder, value); }

        /// <summary>
        /// 应用更新下载源偏好。
        /// 0=官方直连, 1=镜像一, 2=镜像二, 3=自定义镜像。
        /// </summary>
        public int AppUpdatePreferredSource { get => _appUpdatePreferredSource; set => SetProperty(ref _appUpdatePreferredSource, value); }

        /// <summary>
        /// 下载失败时是否按预设顺序自动切换备用源。
        /// </summary>
        public bool AppUpdateAutoFallback { get => _appUpdateAutoFallback; set => SetProperty(ref _appUpdateAutoFallback, value); }

        /// <summary>
        /// 自定义镜像地址。可填写前缀或包含 {url} 占位符的模板。
        /// </summary>
        public string AppUpdateCustomMirrorUrl { get => _appUpdateCustomMirrorUrl; set => SetProperty(ref _appUpdateCustomMirrorUrl, value ?? string.Empty); }

        /// <summary>
        /// GitHub OAuth App 的 Client ID。
        /// </summary>
        public string GitHubOAuthClientId { get => _gitHubOAuthClientId; set => SetProperty(ref _gitHubOAuthClientId, value ?? string.Empty); }

        /// <summary>
        /// 是否已经展示过首次启动引导。
        /// </summary>
        public bool HasShownFirstLaunchGuide { get => _hasShownFirstLaunchGuide; set => SetProperty(ref _hasShownFirstLaunchGuide, value); }

        /// <summary>
        /// 是否已经执行过首次核心功能自动校验。
        /// </summary>
        public bool HasTriggeredInitialCoreValidation { get => _hasTriggeredInitialCoreValidation; set => SetProperty(ref _hasTriggeredInitialCoreValidation, value); }

        /// <summary>
        /// 最近一次核心功能自动校验是否通过。
        /// </summary>
        public bool LastCoreValidationPassed { get => _lastCoreValidationPassed; set => SetProperty(ref _lastCoreValidationPassed, value); }

        /// <summary>
        /// 最近一次核心功能自动校验执行时间（UTC）。
        /// </summary>
        public DateTime LastCoreValidationUtc { get => _lastCoreValidationUtc; set => SetProperty(ref _lastCoreValidationUtc, value); }

        /// <summary>
        /// 最近一次核心功能自动校验摘要。
        /// </summary>
        public string LastCoreValidationSummary { get => _lastCoreValidationSummary; set => SetProperty(ref _lastCoreValidationSummary, value ?? string.Empty); }

    }
}
