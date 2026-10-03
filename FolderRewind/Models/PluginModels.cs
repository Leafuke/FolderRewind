using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    /// <summary>
    /// 插件系统：Host 侧持久化设置（挂在 GlobalSettings 下）。
    /// 注意：这里的结构尽量“稳定、可扩展”，避免未来扩展时破坏已有用户配置。
    /// </summary>
    public class PluginHostSettings : ObservableObject
    {
        private Dictionary<string, bool> _enabledIntent = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Dictionary<string, JsonElement>> _typedSettings = new(StringComparer.OrdinalIgnoreCase);

        private bool _autoCheckUpdates = true;
        /// <summary>
        /// 是否自动检查插件更新
        /// </summary>
        public bool AutoCheckUpdates
        {
            get => _autoCheckUpdates;
            set => SetProperty(ref _autoCheckUpdates, value);
        }

        /// <summary>
        /// v3 user intent, independent from installed and runtime state.
        /// </summary>
        public Dictionary<string, bool> EnabledIntent
        {
            get => _enabledIntent;
            set => _enabledIntent = value == null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Typed JSON settings declared by each plugin's static settings schema.
        /// </summary>
        public Dictionary<string, Dictionary<string, JsonElement>> TypedSettings
        {
            get => _typedSettings;
            set => _typedSettings = value == null
                ? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Dictionary<string, JsonElement>>(value, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 插件运行时信息（用于 UI 展示）。
    /// </summary>
    public class InstalledPluginInfo : ObservableObject
    {
        private bool _isEnabled;
        private bool _requiresRestart;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        [JsonIgnore]
        public string InstallPath { get; set; } = string.Empty;

        [JsonIgnore]
        public string? LoadError { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        /// <summary>
        /// Runtime cleanup 或物理卸载尚未安全完成，请求的插件状态需在 Host 重启后最终生效。
        /// </summary>
        [JsonIgnore]
        public bool RequiresRestart
        {
            get => _requiresRestart;
            set => SetProperty(ref _requiresRestart, value);
        }

        /// <summary>
        /// 插件 GitHub 仓库（格式 owner/repo）
        /// </summary>
        public string? Repository { get; set; }

        /// <summary>
        /// 主页链接
        /// </summary>
        public string? Homepage { get; set; }

        private bool _hasUpdate;
        /// <summary>
        /// 是否有可用更新
        /// </summary>
        [JsonIgnore]
        public bool HasUpdate
        {
            get => _hasUpdate;
            set => SetProperty(ref _hasUpdate, value);
        }

        private string? _latestVersion;
        /// <summary>
        /// 最新版本号
        /// </summary>
        [JsonIgnore]
        public string? LatestVersion
        {
            get => _latestVersion;
            set => SetProperty(ref _latestVersion, value);
        }

        private string? _updateDownloadUrl;
        /// <summary>
        /// 更新下载地址
        /// </summary>
        [JsonIgnore]
        public string? UpdateDownloadUrl
        {
            get => _updateDownloadUrl;
            set => SetProperty(ref _updateDownloadUrl, value);
        }
    }

    /// <summary>
    /// Host 投影到配置类型选择器的稳定选项。显示文本可以随语言变化，
    /// 持久化时始终使用 Config Kind 身份，避免把本地化名称误当成标识符。
    /// </summary>
    public sealed class PluginConfigKindOption
    {
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public ConfigKindReference Kind { get; init; } = new();
        public string RequiredPluginId { get; init; } = string.Empty;
        public bool IsEncrypted { get; init; }

        public string SelectionValue => StableKey;

        public string StableKey => IsEncrypted
            ? "folderrewind.core/encrypted"
            : $"{Kind.OwnerId}/{Kind.KindId}";

        public ConfigKindReference CreateReference()
            => new() { OwnerId = Kind.OwnerId, KindId = Kind.KindId };

        public override string ToString() => DisplayName;
    }
}
