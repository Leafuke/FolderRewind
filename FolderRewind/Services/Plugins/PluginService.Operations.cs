using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.KnotLink;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Services.Plugins
{
    public static partial class PluginService
    {
        public static (bool ShouldHandle, IFolderRewindPlugin? Plugin) CheckPluginWantsToHandleBackup(BackupConfig config)
        {
            if (!IsPluginSystemEnabled()) return (false, null);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (plugin.WantsToHandleBackup(config))
                    {
                        return (true, plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_WantsToHandleBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return (false, null);
        }

        /// <summary>
        /// 检查是否有插件希望接管指定配置的还原
        /// </summary>
        public static (bool ShouldHandle, IFolderRewindPlugin? Plugin) CheckPluginWantsToHandleRestore(BackupConfig config)
        {
            if (!IsPluginSystemEnabled()) return (false, null);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (plugin.WantsToHandleRestore(config))
                    {
                        return (true, plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_WantsToHandleRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return (false, null);
        }

        /// <summary>
        /// 调用插件执行备份
        /// </summary>
        public static async Task<PluginBackupResult> InvokePluginBackupAsync(
            IFolderRewindPlugin plugin,
            BackupConfig config,
            ManagedFolder folder,
            string comment,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                var settings = GetPluginSettings(plugin.Manifest.Id);
                return await plugin.PerformBackupAsync(config, folder, comment, settings, progressCallback);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_PerformBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                return new PluginBackupResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 调用插件执行还原
        /// </summary>
        public static async Task<PluginRestoreResult> InvokePluginRestoreAsync(
            IFolderRewindPlugin plugin,
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                var settings = GetPluginSettings(plugin.Manifest.Id);
                return await plugin.PerformRestoreAsync(config, folder, archiveFileName, settings, progressCallback);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_PerformRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                return new PluginRestoreResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 调用插件批量创建配置
        /// </summary>
        public static PluginCreateConfigResult InvokeCreateConfigs(string selectedRootPath, string? configType = null)
        {
            if (!IsPluginSystemEnabled()) return new PluginCreateConfigResult { Handled = false };
            if (string.IsNullOrWhiteSpace(selectedRootPath)) return new PluginCreateConfigResult { Handled = false };

            var typeFilter = string.IsNullOrWhiteSpace(configType) ? null : configType;
            if (string.Equals(typeFilter, "Default", StringComparison.OrdinalIgnoreCase))
            {
                // Default 不是插件类型，不走插件批量创建
                return new PluginCreateConfigResult { Handled = false };
            }

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(typeFilter))
                    {
                        bool canHandleType = false;
                        try
                        {
                            canHandleType = plugin.CanHandleConfigType(typeFilter);
                        }
                        catch
                        {
                            // ignore; fallback to supported types list
                        }

                        if (!canHandleType)
                        {
                            var supported = plugin.GetSupportedConfigTypes();
                            canHandleType = supported != null && supported.Any(t => string.Equals(t, typeFilter, StringComparison.OrdinalIgnoreCase));
                        }

                        if (!canHandleType)
                        {
                            continue;
                        }
                    }

                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var result = plugin.TryCreateConfigs(selectedRootPath, settings);
                    if (result.Handled)
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_TryCreateConfigsFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return new PluginCreateConfigResult { Handled = false };
        }

        /// <summary>
        /// 从 zip 安装插件（zip 内需包含 manifest.json）。
        /// 目标目录：plugins/{pluginId}/...
        /// </summary>
    }
}
