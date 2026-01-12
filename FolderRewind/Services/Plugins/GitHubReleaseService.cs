using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    internal static class GitHubReleaseService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FolderRewind", "1.0"));
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static async Task<IReadOnlyList<GitHubReleaseAsset>> GetLatestReleaseAssetsAsync(string owner, string repo, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                return Array.Empty<GitHubReleaseAsset>();

            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
                    return Array.Empty<GitHubReleaseAsset>();

                var list = new List<GitHubReleaseAsset>();
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var dl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() : null;
                    var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dl)) continue;

                    list.Add(new GitHubReleaseAsset
                    {
                        Name = name!,
                        DownloadUrl = dl!,
                        SizeBytes = size
                    });
                }

                return list;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginStore_GetReleaseFailed_Log", ex.Message), "GitHubReleaseService", ex);
                return Array.Empty<GitHubReleaseAsset>();
            }
        }

        public sealed class GitHubReleaseAsset
        {
            public string Name { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
        }

        public static async Task<byte[]> DownloadAssetAsync(string url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
    }
}
