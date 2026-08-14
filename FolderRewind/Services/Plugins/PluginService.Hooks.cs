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
        public static string? InvokeBeforeBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions? invocationOptions = null)
        {
            if (!IsPluginSystemEnabled()) return null;

            invocationOptions ??= BackupInvocationOptions.Default;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var newPath = plugin is IFolderRewindBackupPreparationProvider preparationProvider
                        ? preparationProvider.OnBeforeBackupFolder(config, folder, invocationOptions, settings)
                        : plugin.OnBeforeBackupFolder(config, folder, settings);
                    if (!string.IsNullOrWhiteSpace(newPath))
                    {
                        // 允许多个插件串联修改路径：使用最后一个返回的路径
                        folder = new ManagedFolder { Path = newPath, DisplayName = folder.DisplayName, Description = folder.Description, IsFavorite = folder.IsFavorite, CoverImagePath = folder.CoverImagePath };
                        return newPath;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_BeforeBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return null;
        }

        public static void InvokeAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName)
        {
            if (!IsPluginSystemEnabled()) return;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    plugin.OnAfterBackupFolder(config, folder, success, generatedArchiveFileName, settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_AfterBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }
        }

        /// <summary>
        /// 还原前钩子：调用所有已启用插件的 OnBeforeRestoreFolder。
        /// 返回每个插件的 (pluginId, state) 列表，供 InvokeAfterRestoreFolder 使用。
        /// </summary>
        public static List<(string PluginId, IFolderRewindPlugin Plugin, object? State)> InvokeBeforeRestoreFolder(
            BackupConfig config, ManagedFolder folder, string archiveFileName)
        {
            var results = new List<(string, IFolderRewindPlugin, object?)>();
            if (!IsPluginSystemEnabled()) return results;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var state = plugin.OnBeforeRestoreFolder(config, folder, archiveFileName, settings);
                    results.Add((plugin.Manifest.Id, plugin, state));
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_BeforeRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                    results.Add((plugin.Manifest.Id, plugin, null));
                }
            }

            return results;
        }

        /// <summary>
        /// 在宿主创建还原任务或产生还原副作用前调用可选拦截器。
        /// 第一个 Handled/Blocked 结果终止聚合；插件异常仅记录并继续。
        /// </summary>
        public static async Task<(string PluginId, PluginRestoreInterceptionResult Result)> TryInterceptRestoreFolderAsync(
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            CancellationToken cancellationToken = default)
        {
            if (!IsPluginSystemEnabled())
            {
                return (string.Empty, PluginRestoreInterceptionResult.Continue());
            }

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                if (plugin is not IFolderRewindRestoreInterceptor interceptor)
                {
                    continue;
                }

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var result = await interceptor.TryInterceptRestoreAsync(
                        config,
                        folder,
                        archiveFileName,
                        settings,
                        cancellationToken).ConfigureAwait(false)
                        ?? PluginRestoreInterceptionResult.Continue();
                    if (result.Status != PluginRestoreInterceptionStatus.Continue)
                    {
                        return (plugin.Manifest.Id, result);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.LogError(
                        I18n.Format("PluginService_BeforeRestoreFailed", plugin.Manifest.Id, ex.Message),
                        "PluginService",
                        ex);
                }
            }

            return (string.Empty, PluginRestoreInterceptionResult.Continue());
        }

        /// <summary>
        /// 还原后钩子：调用所有已启用插件的 OnAfterRestoreFolder。
        /// pluginStates 为 InvokeBeforeRestoreFolder 的返回值。
        /// </summary>
        public static void InvokeAfterRestoreFolder(
            BackupConfig config, ManagedFolder folder, bool success, string archiveFileName,
            List<(string PluginId, IFolderRewindPlugin Plugin, object? State)>? pluginStates)
        {
            if (!IsPluginSystemEnabled()) return;
            if (pluginStates == null || pluginStates.Count == 0) return;

            foreach (var (pluginId, plugin, state) in pluginStates)
            {
                try
                {
                    var settings = GetPluginSettings(pluginId);
                    plugin.OnAfterRestoreFolder(config, folder, success, archiveFileName, state, settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_AfterRestoreFailed", pluginId, ex.Message), "PluginService", ex);
                }
            }
        }

        public static IReadOnlyList<ManagedFolder> InvokeDiscoverManagedFolders(string selectedRootPath)
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<ManagedFolder>();
            if (string.IsNullOrWhiteSpace(selectedRootPath)) return Array.Empty<ManagedFolder>();

            var results = new List<ManagedFolder>();
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var discovered = plugin.TryDiscoverManagedFolders(selectedRootPath, settings);
                    if (discovered != null && discovered.Count > 0)
                    {
                        results.AddRange(discovered);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_DiscoverManagedFoldersFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return results;
        }

    }
}
