using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// 插件商店：从 GitHub Release 下载插件 zip，并调用 PluginService 安装。
    /// 约定：Release 资产应为 zip，且 zip 根目录包含 manifest.json。
    /// </summary>
    public static class PluginStoreService
    {
        public static bool TryParseRepo(string repoText, out string owner, out string repo)
        {
            owner = string.Empty;
            repo = string.Empty;

            if (string.IsNullOrWhiteSpace(repoText)) return false;
            var parts = repoText.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return false;

            owner = parts[0];
            repo = parts[1];
            return !(string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo));
        }

        public static async Task<IReadOnlyList<PluginStoreAssetItem>> GetLatestAssetsAsync(string owner, string repo, CancellationToken ct)
        {
            var assets = await GitHubReleaseService.GetLatestReleaseAssetsAsync(owner, repo, ct);
            return assets
                .Select(a => new PluginStoreAssetItem
                {
                    Name = a.Name,
                    DownloadUrl = a.DownloadUrl,
                    SizeBytes = a.SizeBytes
                })
                .ToList();
        }

        public static async Task<(bool Success, string Message)> DownloadAndInstallAsync(PluginStoreAssetItem asset, CancellationToken ct)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
                return (false, "无效的下载项");

            // 当前版本仅支持 zip
            if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (false, "当前仅支持 zip 插件包（需要包含 manifest.json）");

            try
            {
                Directory.CreateDirectory(PluginService.PluginRootDirectory);
                var tempDir = Path.Combine(PluginService.PluginRootDirectory, "_downloads");
                Directory.CreateDirectory(tempDir);

                var tempZip = Path.Combine(tempDir, $"{Guid.NewGuid():N}-{asset.Name}");

                var bytes = await GitHubReleaseService.DownloadAssetAsync(asset.DownloadUrl, ct);
                await File.WriteAllBytesAsync(tempZip, bytes, ct);

                var res = await PluginService.InstallFromZipAsync(tempZip, ct);

                try { File.Delete(tempZip); } catch { }

                return res;
            }
            catch (OperationCanceledException)
            {
                return (false, "已取消");
            }
            catch (Exception ex)
            {
                LogService.LogError($"[PluginStore] 下载/安装失败：{ex.Message}", "PluginStoreService", ex);
                return (false, $"下载/安装失败：{ex.Message}");
            }
        }
    }
}
