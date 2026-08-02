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
        /// <summary>
        /// 检查所有已安装插件的更新
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <param name="respectAutoCheckSetting">是否尊重自动检查更新设置</param>
        public static async Task CheckAllPluginUpdatesAsync(CancellationToken ct = default, bool respectAutoCheckSetting = true)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;
            if (respectAutoCheckSetting && !settings.AutoCheckUpdates) return;

            foreach (var plugin in _installed.ToList())
            {
                if (ct.IsCancellationRequested) break;
                await CheckPluginUpdateAsync(plugin, ct);
            }
        }

        /// <summary>
        /// 检查单个插件的更新
        /// </summary>
        public static async Task CheckPluginUpdateAsync(InstalledPluginInfo plugin, CancellationToken ct = default)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Repository)) return;

            try
            {
                // 每次检查先清空旧状态，避免残留状态导致“误报有更新”
                plugin.HasUpdate = false;
                plugin.LatestVersion = null;
                plugin.UpdateDownloadUrl = null;

                var (hasUpdate, latestVersion, downloadUrl) = await CheckGitHubReleaseAsync(plugin.Repository, plugin.Version, ct);

                // 没有可下载链接时，不应视为可更新
                var actionable = hasUpdate && !string.IsNullOrWhiteSpace(downloadUrl);

                plugin.HasUpdate = actionable;
                plugin.LatestVersion = actionable ? latestVersion : null;
                plugin.UpdateDownloadUrl = actionable ? downloadUrl : null;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_CheckUpdateFailed", plugin.Id, ex.Message), "PluginService");
            }
        }

        /// <summary>
        /// 检查 GitHub Release 获取最新版本
        /// </summary>
        private static async Task<(bool HasUpdate, string? LatestVersion, string? DownloadUrl)> CheckGitHubReleaseAsync(
            string repository, string currentVersion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(repository)) return (false, null, null);

            // 格式: owner/repo
            var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return (false, null, null);

            var release = await GitHubReleaseService.GetLatestReleaseAsync(parts[0], parts[1], ct);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage) || string.IsNullOrWhiteSpace(release.TagName))
            {
                return (false, null, null);
            }

            var tagName = release.TagName;
            if (string.IsNullOrWhiteSpace(tagName)) return (false, null, null);

            // 清理版本号（移除 v 前缀）
            var latestVersion = tagName.TrimStart('v', 'V');
            var current = currentVersion?.TrimStart('v', 'V') ?? "0.0.0";

            // 比较版本
            bool hasUpdate = false;
            try
            {
                var latestVer = Version.Parse(latestVersion);
                var currentVer = Version.Parse(current);
                hasUpdate = latestVer > currentVer;
            }
            catch
            {
                // 版本格式不标准，使用字符串比较
                hasUpdate = !string.Equals(latestVersion, current, StringComparison.OrdinalIgnoreCase);
            }

            var downloadUrl = release.Assets
                .FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = release.ZipballUrl;
            }

            return (hasUpdate, latestVersion, downloadUrl);
        }

        /// <summary>
        /// 从 URL 更新插件
        /// </summary>
        public static async Task<(bool Success, string Message)> UpdatePluginFromUrlAsync(
            InstalledPluginInfo plugin, CancellationToken ct = default)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
            {
                return (false, _rl.GetString("PluginService_NoUpdateUrl"));
            }

            try
            {
                var wasEnabled = GetPluginEnabled(plugin.Id);

                // 保存到临时文件
                var tempFile = Path.Combine(Path.GetTempPath(), $"FolderRewind_Plugin_{plugin.Id}_{Guid.NewGuid():N}.zip");
                var bytes = await GitHubReleaseService.DownloadAssetAsync(plugin.UpdateDownloadUrl, ct);
                await using (var fs = File.Create(tempFile))
                {
                    await fs.WriteAsync(bytes, ct);
                }

                // 先卸载旧版本
                var unloadSuccess = TryUnloadPlugin(plugin.Id);
                if (!unloadSuccess)
                {
                    QueuePendingUpdate(plugin.Id, tempFile, wasEnabled);
                    return (true, _rl.GetString("PluginService_UpdatePendingRestart"));
                }

                // 安装新版本
                var result = await InstallFromZipAsync(tempFile, ct);

                if (!result.Success)
                {
                    // 大概率是文件占用导致覆盖失败，回退为“下次启动应用更新”
                    QueuePendingUpdate(plugin.Id, tempFile, wasEnabled);
                    return (true, _rl.GetString("PluginService_UpdatePendingRestart"));
                }

                // 清理临时文件
                try { File.Delete(tempFile); } catch { }

                if (result.Success)
                {
                    // 重新启用插件
                    if (wasEnabled)
                    {
                        TryLoadPlugin(plugin.Id);
                    }

                    // 清除更新标记
                    plugin.HasUpdate = false;
                    plugin.LatestVersion = null;
                    plugin.UpdateDownloadUrl = null;
                }

                return result;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_UpdateFailed", plugin.Id, ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_UpdateFailed"), ex.Message));
            }
        }

    }
}
