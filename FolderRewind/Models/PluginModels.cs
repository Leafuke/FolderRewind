using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    /// <summary>
    /// 插件系统：Host 侧持久化设置（挂在 GlobalSettings 下）。
    /// 注意：这里的结构尽量“稳定、可扩展”，避免未来扩展时破坏已有用户配置。
    /// </summary>
    public class PluginHostSettings : ObservableObject
    {
        private bool _enabled = false;
        private string _storeRepo = string.Empty;

        /// <summary>
        /// 插件系统总开关。关闭后：不执行插件逻辑、商店入口禁用。
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }

        /// <summary>
        /// 插件商店 GitHub 仓库："owner/repo"。
        /// 为空时表示未配置商店来源。
        /// </summary>
        public string StoreRepo
        {
            get => _storeRepo;
            set => SetProperty(ref _storeRepo, value ?? string.Empty);
        }

        /// <summary>
        /// 每个插件的启用状态。
        /// Key: PluginId
        /// </summary>
        public Dictionary<string, bool> PluginEnabled { get; set; } = new();

        /// <summary>
        /// 每个插件的设置值（由插件定义键名，Host 负责保存与回传）。
        /// Key: PluginId -> (SettingKey -> stringValue)
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> PluginSettings { get; set; } = new();
    }

    /// <summary>
    /// 插件包 Manifest（用于 Host 扫描安装目录；通常来自插件 zip 内的 manifest.json）。
    /// </summary>
    public class PluginInstallManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 可选：多语言名称（key: BCP-47 tag，例如 "en-US" / "zh-CN"）。
        /// </summary>
        public Dictionary<string, string>? LocalizedName { get; set; }

        /// <summary>
        /// 可选：多语言描述（key: BCP-47 tag，例如 "en-US" / "zh-CN"）。
        /// </summary>
        public Dictionary<string, string>? LocalizedDescription { get; set; }

        /// <summary>
        /// 插件入口程序集文件名（相对插件目录），例如 "MyPlugin.dll"。
        /// </summary>
        public string EntryAssembly { get; set; } = string.Empty;

        /// <summary>
        /// 插件入口类型全名，例如 "MyPlugin.MainPlugin"。
        /// </summary>
        public string EntryType { get; set; } = string.Empty;

        /// <summary>
        /// 最低 Host 版本（可选）。
        /// </summary>
        public string? MinHostVersion { get; set; }

        /// <summary>
        /// 备注/主页等（可选）。
        /// </summary>
        public string? Homepage { get; set; }
    }

    public enum PluginSettingType
    {
        String = 0,
        Boolean = 1,
        Integer = 2,
        Path = 3
    }

    /// <summary>
    /// 插件设置项定义。Host 根据定义渲染简单设置 UI。
    /// </summary>
    public class PluginSettingDefinition
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public PluginSettingType Type { get; set; } = PluginSettingType.String;
        public string? DefaultValue { get; set; }
        public bool IsRequired { get; set; } = false;
    }

    /// <summary>
    /// 插件运行时信息（用于 UI 展示）。
    /// </summary>
    public class InstalledPluginInfo : ObservableObject
    {
        private bool _isEnabled;

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
    }
}
