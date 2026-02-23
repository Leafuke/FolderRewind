using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// 插件备份结果
    /// </summary>
    public class PluginBackupResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }
        /// <summary>生成的归档文件名（用于历史记录）</summary>
        public string? GeneratedFileName { get; set; }
        /// <summary>错误或状态消息</summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// 插件还原结果
    /// </summary>
    public class PluginRestoreResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }
        /// <summary>错误或状态消息</summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// 插件创建配置结果
    /// </summary>
    public class PluginCreateConfigResult
    {
        /// <summary>插件是否处理了此请求</summary>
        public bool Handled { get; set; }
        /// <summary>创建的配置列表</summary>
        public IReadOnlyList<BackupConfig>? CreatedConfigs { get; set; }
        /// <summary>状态消息</summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// FolderRewind 插件接口 v2。
    /// 
    /// 设计目标：
    /// 1) 让插件可以"扩展配置管理方式"（如 Minecraft 存档自动发现/分类）；
    /// 2) 让插件可以介入备份/还原流程（如热备份/快照目录）；
    /// 3) 让插件可以完全接管备份/还原逻辑（如特殊格式处理）；
    /// 4) 让插件可以定义自己的配置类型（ConfigType）。
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
        /// 由 Host 调用：在插件加载后注入宿主上下文。
        /// 插件可缓存此对象，用于主动发起 KnotLink 广播/查询等操作。
        /// </summary>
        void SetHostContext(PluginHostContext hostContext) { }

        #region 备份钩子

        /// <summary>
        /// 备份前钩子：允许插件创建快照/替换源目录。
        /// 返回 null 表示不修改源目录；返回新路径则使用该路径作为备份源。
        /// </summary>
        string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder, IReadOnlyDictionary<string, string> settingsValues);

        /// <summary>
        /// 备份后钩子：用于清理快照/记录额外元数据等。
        /// </summary>
        void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues);

        #endregion

        #region 配置类型与发现

        /// <summary>
        /// 获取此插件支持的配置类型列表。
        /// 例如 Minecraft 插件可返回 ["Minecraft Saves"]。
        /// 返回空列表表示插件不定义自己的配置类型。
        /// </summary>
        IReadOnlyList<string> GetSupportedConfigTypes() => Array.Empty<string>();

        /// <summary>
        /// 检查插件是否能处理指定的配置类型。
        /// </summary>
        bool CanHandleConfigType(string configType) => false;

        /// <summary>
        /// 配置扩展：当用户选择一个根目录时，插件可解析并返回一组 ManagedFolder。
        /// 用于像 Minecraft 这类"从 .minecraft 自动发现 saves"。
        /// </summary>
        IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues);

        /// <summary>
        /// 批量创建配置：当用户选择使用此插件的特殊创建流程时调用。
        /// 返回 Handled=true 表示插件已处理，Host 应使用 CreatedConfigs。
        /// </summary>
        PluginCreateConfigResult TryCreateConfigs(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
            => new PluginCreateConfigResult { Handled = false };

        #endregion

        #region 完全接管备份/还原（可选实现）

        /// <summary>
        /// 是否希望完全接管此配置的备份流程。
        /// 返回 true 时，Host 将调用 PerformBackupAsync 而非内置备份逻辑。
        /// </summary>
        bool WantsToHandleBackup(BackupConfig config) => false;

        /// <summary>
        /// 是否希望完全接管此配置的还原流程。
        /// 返回 true 时，Host 将调用 PerformRestoreAsync 而非内置还原逻辑。
        /// </summary>
        bool WantsToHandleRestore(BackupConfig config) => false;

        /// <summary>
        /// 执行完整的备份流程（当 WantsToHandleBackup 返回 true 时调用）。
        /// </summary>
        Task<PluginBackupResult> PerformBackupAsync(
            BackupConfig config,
            ManagedFolder folder,
            string comment,
            IReadOnlyDictionary<string, string> settingsValues,
            Action<double, string>? progressCallback = null)
            => Task.FromResult(new PluginBackupResult { Success = false, Message = I18n.Format("Plugins_NotImplemented") });

        /// <summary>
        /// 执行完整的还原流程（当 WantsToHandleRestore 返回 true 时调用）。
        /// </summary>
        Task<PluginRestoreResult> PerformRestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            IReadOnlyDictionary<string, string> settingsValues,
            Action<double, string>? progressCallback = null)
            => Task.FromResult(new PluginRestoreResult { Success = false, Message = I18n.Format("Plugins_NotImplemented") });

        #endregion
    }
}
