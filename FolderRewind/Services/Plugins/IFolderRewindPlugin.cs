using FolderRewind.Models;
using System;
using System.Collections.Generic;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// FolderRewind 插件接口（最小可用版本）。
    /// 
    /// 设计目标：
    /// 1) 让插件可以“扩展配置管理方式”（如 Minecraft 存档自动发现/分类）；
    /// 2) 让插件可以介入备份流程（如热备份/快照目录）。
    /// 
    /// 注意：插件运行在同一进程中，必须做好异常处理；Host 也会做隔离与日志。
    /// </summary>
    public interface IFolderRewindPlugin
    {
        /// <summary>
        /// 插件基本信息（用于 UI 与日志）。
        /// </summary>
        PluginInstallManifest Manifest { get; }

        /// <summary>
        /// 插件可选设置定义。Host 会保存用户填写的值，并在调用插件时通过 settingsValues 传回。
        /// </summary>
        IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions();

        /// <summary>
        /// 由 Host 调用：在插件被启用时触发一次初始化。
        /// settingsValues: Host 持久化的该插件设置（Key -> stringValue）。
        /// </summary>
        void Initialize(IReadOnlyDictionary<string, string> settingsValues);

        /// <summary>
        /// 备份前钩子：允许插件创建快照/替换源目录。
        /// 返回 null 表示不修改源目录；返回新路径则使用该路径作为备份源。
        /// </summary>
        string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder, IReadOnlyDictionary<string, string> settingsValues);

        /// <summary>
        /// 备份后钩子：用于清理快照/记录额外元数据等。
        /// </summary>
        void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues);

        /// <summary>
        /// 配置扩展：当用户选择一个根目录时，插件可解析并返回一组 ManagedFolder。
        /// 用于像 Minecraft 这类“从 .minecraft 自动发现 saves”。
        /// Host 未来可以在合适的 UI 中调用（本次先把能力定义好）。
        /// </summary>
        IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues);
    }
}
