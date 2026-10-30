using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    internal static partial class GitHubReleaseService
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

        public static async Task<GitHubLatestReleaseResult> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                return new GitHubLatestReleaseResult { ErrorMessage = "invalid-repo" };

            var rawUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            string? lastError = null;

            foreach (var candidate in DownloadSourceService.BuildCandidates(rawUrl))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    return ParseMetadata(doc.RootElement, candidate.Url,
                        resp.RequestMessage?.RequestUri?.OriginalString, rawUrl, candidate.DisplayName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    LogService.LogWarning(
                        $"GitHub release fetch failed from {candidate.DisplayName}: {candidate.Url} - {ex.Message}",
                        nameof(GitHubReleaseService));
                }
            }

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                LogService.LogError(I18n.Format("PluginStore_GetReleaseFailed_Log", lastError), "GitHubReleaseService");
            }

            return new GitHubLatestReleaseResult { ErrorMessage = lastError ?? "release-fetch-failed" };
        }

        internal static Task<byte[]> DownloadVerifiedAssetAsync(string url, string expectedSha256,
            CancellationToken ct, long maximumBytes = ToolArchiveInstaller.MaximumArchiveBytes)
            => VerifiedAssetDownloader.DownloadAsync(Http, DownloadSourceService.BuildCandidates(url).Select(c => c.Url),
                expectedSha256, maximumBytes, ct,
                (source, error) => LogService.LogWarning($"Verified download failed: {source} - {error.Message}", nameof(GitHubReleaseService)));

        public static async Task<byte[]> DownloadAssetAsync(string url, CancellationToken ct, long maximumBytes = long.MaxValue)
        {
            Exception? lastError = null;

            foreach (var candidate in DownloadSourceService.BuildCandidates(url))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    if (resp.Content.Headers.ContentLength > maximumBytes) throw new IOException("Download exceeds its size bound.");
                    await using var input = await resp.Content.ReadAsStreamAsync(ct);
                    using var output = new MemoryStream();
                    var buffer = new byte[65536]; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        if (output.Length + count > maximumBytes) throw new IOException("Download exceeds its size bound.");
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    LogService.LogWarning(
                        $"GitHub asset download failed from {candidate.DisplayName}: {candidate.Url} - {ex.Message}",
                        nameof(GitHubReleaseService));
                }
            }

            if (lastError != null)
            {
                throw lastError;
            }

            throw new InvalidOperationException("No download source available.");
        }
    }
}
