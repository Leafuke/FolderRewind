using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// 插件商店：从 GitHub Release 下载插件 zip，并调用 PluginService 安装。
    /// 约定：Release 资产应为 zip，且 zip 根目录包含 manifest.json。
    /// </summary>
    public static class PluginStoreService
    {
        private static readonly ResourceLoader _rl = ResourceLoader.GetForViewIndependentUse();

        public sealed class PluginStoreLoadResult
        {
            public IReadOnlyList<PluginStoreAssetItem> Items { get; set; } = Array.Empty<PluginStoreAssetItem>();
            public string? Summary { get; set; }
            public string? ErrorMessage { get; set; }
        }

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

        public static async Task<PluginStoreLoadResult> GetLatestAssetsAsync(string owner, string repo, CancellationToken ct)
        {
            var release = await GitHubReleaseService.GetLatestReleaseAsync(owner, repo, ct);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage))
            {
                return new PluginStoreLoadResult
                {
                    ErrorMessage = string.Format(_rl.GetString("PluginStore_DownloadInstallFailed"), release.ErrorMessage)
                };
            }

            var items = release.Assets
                .Select(a => new PluginStoreAssetItem
                {
                    Name = a.Name,
                    DownloadUrl = a.DownloadUrl,
                    SizeBytes = a.SizeBytes,
                    DownloadCount = a.DownloadCount,
                    UpdatedAt = a.UpdatedAt,
                    ReleaseTag = release.TagName
                })
                .ToList();

            var summary = string.IsNullOrWhiteSpace(release.ReleaseName)
                ? release.TagName
                : string.IsNullOrWhiteSpace(release.TagName)
                    ? release.ReleaseName
                    : $"{release.ReleaseName} ({release.TagName})";

            return new PluginStoreLoadResult
            {
                Items = items,
                Summary = summary
            };
        }

        public static async Task<(bool Success, string Message)> DownloadAndInstallAsync(PluginStoreAssetItem asset, CancellationToken ct)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
                return (false, _rl.GetString("PluginStore_InvalidItem"));

            // 当前版本仅支持 zip
            if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (false, _rl.GetString("PluginStore_OnlyZipSupported"));

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
                return (false, _rl.GetString("Common_Canceled"));
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginStore_Log_DownloadInstallFailed", ex.Message), "PluginStoreService", ex);
                return (false, string.Format(_rl.GetString("PluginStore_DownloadInstallFailed"), ex.Message));
            }
        }
    }
}
