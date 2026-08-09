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
        private bool _isEncrypted = false; // 是否为加密配置
        private DiscoveryOrigin? _discoveryOrigin;

        // 核心路径
        public string Id { get => _id; set => SetProperty(ref _id, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string DestinationPath { get => _destinationPath; set => SetProperty(ref _destinationPath, value); }

        /// <summary>
        /// 配置类型。默认为 "Default"。
        /// 插件可以定义自己的配置类型，如 "Minecraft Saves"。
        /// </summary>
        public string ConfigType { get => _configType; set => SetProperty(ref _configType, value ?? "Default"); }

        /// <summary>
        /// 是否为加密配置。加密配置的备份将使用 7-Zip 加密，密码通过 EncryptionService 安全存储。
        /// 密码一旦设置无法更改。
        /// </summary>
        public bool IsEncrypted { get => _isEncrypted; set => SetProperty(ref _isEncrypted, value); }

        public DiscoveryOrigin? DiscoveryOrigin { get => _discoveryOrigin; set => SetProperty(ref _discoveryOrigin, value); }

        /// <summary>
        /// 是否为 Minecraft Saves 配置类型（用于 UI 卡片徽标显示）。
        /// </summary>
        [JsonIgnore]
        public bool IsMinecraftConfig => string.Equals(_configType, "Minecraft Saves", StringComparison.OrdinalIgnoreCase);

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

        // 备份范围。默认完整范围；插件可以按配置提供“Minecraft 指定区域”等范围策略。
        public BackupScopeSettings BackupScope { get; set; } = new();

        // 云上传设置（通过外部工具执行）
        public CloudSettings Cloud { get; set; } = new();

        // 扩展属性 (用于插件，如 Minecraft 插件存储 rcon 端口等)
        public Dictionary<string, string> ExtendedProperties { get; set; } = new();
    }

    /// <summary>
    /// 配置级备份范围设置。
    /// 这里不保存插件全局设置，而是保存“这个配置”选用了哪个插件范围和对应参数。
    /// </summary>
    public class BackupScopeSettings : ObservableObject
    {
        private string _pluginScopeId = string.Empty;
        private Dictionary<string, string> _parameters = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 插件范围 ID。为空表示完整范围，沿用普通备份行为。
        /// </summary>
        public string PluginScopeId
        {
            get => _pluginScopeId;
            set => SetProperty(ref _pluginScopeId, value?.Trim() ?? string.Empty);
        }

        /// <summary>
        /// 插件范围参数。Key 由插件声明，Host 只负责保存和传递。
        /// </summary>
        public Dictionary<string, string> Parameters
        {
            get => _parameters;
            set => SetProperty(ref _parameters, value == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase));
        }

        [JsonIgnore]
        public bool IsPluginScopeEnabled => !string.IsNullOrWhiteSpace(PluginScopeId);
    }

    /// <summary>
    /// 被管理的源文件夹
    /// </summary>
    public class ManagedFolder : ObservableObject
    {
        private string _path = "";
        private string _displayName = "";
        private string _description = "";
        private string _statusText = I18n.Format("FolderManager_Status");
        private string _lastBackupTime = I18n.Format("FolderManager_NeverBackedUp");
        private bool _isFavorite;
        private string _coverImagePath = ""; // 对应封面图片路径
        private BackupSourceScope _sourceScope = new();

        // 核心路径
        public string Path
        {
            get => _path;
            set
            {
                var safeValue = value ?? string.Empty;
                SetProperty(ref _path, safeValue);
                if (string.IsNullOrEmpty(_displayName)) DisplayName = System.IO.Path.GetFileName(safeValue);
            }
        }

        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value ?? string.Empty); }

        public string Description { get => _description; set => SetProperty(ref _description, value ?? string.Empty); }

        public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }

        public string LastBackupTime { get => _lastBackupTime; set => SetProperty(ref _lastBackupTime, value ?? string.Empty); }

        [JsonIgnore] // 运行时状态，不需要存Json
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value ?? string.Empty); }

        public string CoverImagePath { get => _coverImagePath; set => SetProperty(ref _coverImagePath, value ?? string.Empty); }

        /// <summary>
        /// 该来源允许进入备份的最大文件集合。配置过滤器和插件范围只能继续缩小它。
        /// </summary>
        public BackupSourceScope SourceScope
        {
            get => _sourceScope;
            set => SetProperty(ref _sourceScope, value ?? new BackupSourceScope());
        }
    }

    public enum BackupMode
    {
        Full = 0,       // 全量备份：每次生成独立完整包
        Incremental = 1,// 增量(Smart)备份：仅备份变化文件，依赖元数据
        Overwrite = 2   // 覆写备份：使用 7z update 指令更新现有包
    }

    public enum BackupDeleteMode
    {
        RecordOnly = 0,
        LocalArchiveOnly = 1,
        LocalArchiveAndRecord = 2
    }

}
