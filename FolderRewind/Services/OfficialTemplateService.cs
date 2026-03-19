using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class OfficialTemplateService
    {
        private const string OfficialIndexUrl = "https://raw.githubusercontent.com/Leafuke/folderrewind-official-templates/main/index.json";
        private const string MirrorIndexUrl = "https://cdn.jsdelivr.net/gh/Leafuke/folderrewind-official-templates@main/index.json";
        private static readonly HttpClient Http = CreateClient();
        private static readonly Regex ShareCodeRegex = new("^[A-HJ-NP-Z2-9]{5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal sealed class FetchIndexResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public IReadOnlyList<RemoteTemplateIndexItem> Templates { get; init; } = Array.Empty<RemoteTemplateIndexItem>();
            public bool UsedCache { get; init; }
            public string SourceDisplayName { get; init; } = string.Empty;
        }

        internal sealed class DownloadTemplateResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public string LocalPath { get; init; } = string.Empty;
            public ConfigTemplate? Template { get; init; }
            public RemoteTemplateIndexItem? IndexItem { get; init; }
        }

        public static bool IsValidShareCode(string? shareCode)
        {
            return !string.IsNullOrWhiteSpace(shareCode)
                && ShareCodeRegex.IsMatch(shareCode.Trim().ToUpperInvariant());
        }

        public static async Task<FetchIndexResult> GetIndexAsync(bool allowCachedFallback = true, CancellationToken ct = default)
        {
            var candidates = new[]
            {
                (Url: OfficialIndexUrl, DisplayName: I18n.GetString("OfficialTemplates_SourceOfficial")),
                (Url: MirrorIndexUrl, DisplayName: I18n.GetString("OfficialTemplates_SourceMirror"))
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync(ct);
                    var templates = DeserializeIndex(json);
                    if (templates.Count == 0)
                    {
                        continue;
                    }

                    WriteCachedIndex(json);
                    return new FetchIndexResult
                    {
                        Success = true,
                        Templates = templates,
                        SourceDisplayName = candidate.DisplayName
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Log(I18n.Format("OfficialTemplates_FetchIndexFailedLog", candidate.Url, ex.Message), LogLevel.Warning);
                }
            }

            if (allowCachedFallback && TryReadCachedIndex(out var cachedTemplates))
            {
                return new FetchIndexResult
                {
                    Success = true,
                    Templates = cachedTemplates,
                    UsedCache = true,
                    SourceDisplayName = I18n.GetString("OfficialTemplates_SourceCache"),
                    Message = I18n.GetString("OfficialTemplates_UsingCachedIndex")
                };
            }

            return new FetchIndexResult
            {
                Success = false,
                Message = I18n.GetString("OfficialTemplates_FetchIndexFailed")
            };
        }

        public static async Task<DownloadTemplateResult> DownloadTemplateAsync(RemoteTemplateIndexItem? item, CancellationToken ct = default)
        {
            if (item == null)
            {
                return new DownloadTemplateResult
                {
                    Success = false,
                    Message = I18n.GetString("OfficialTemplates_TemplateNotFound")
                };
            }

            if (item.IsDisabled)
            {
                return new DownloadTemplateResult
                {
                    Success = false,
                    Message = I18n.GetString("OfficialTemplates_TemplateDisabled"),
                    IndexItem = item
                };
            }

            if (string.IsNullOrWhiteSpace(item.FileUrl))
            {
                return new DownloadTemplateResult
                {
                    Success = false,
                    Message = I18n.GetString("OfficialTemplates_TemplateUrlMissing"),
                    IndexItem = item
                };
            }

            var cachePath = GetTemplateCachePath(item.ShareCode);
            var tempPath = cachePath + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                using var request = new HttpRequestMessage(HttpMethod.Get, item.FileUrl);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await source.CopyToAsync(target, ct);
                }

                var actualHash = await ComputeFileSha256Async(tempPath, ct);
                if (!string.IsNullOrWhiteSpace(item.Sha256)
                    && !string.Equals(actualHash, item.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(tempPath);
                    return new DownloadTemplateResult
                    {
                        Success = false,
                        Message = I18n.GetString("OfficialTemplates_HashMismatch"),
                        IndexItem = item
                    };
                }

                File.Move(tempPath, cachePath, true);

                if (!TemplateService.TryLoadTemplateFromPackage(cachePath, out var template, out var message) || template == null)
                {
                    return new DownloadTemplateResult
                    {
                        Success = false,
                        Message = string.IsNullOrWhiteSpace(message) ? I18n.GetString("OfficialTemplates_LoadFailed") : message,
                        IndexItem = item
                    };
                }

                var validation = TemplateService.ValidateTemplateForOfficialSharing(template);
                if (!validation.Success)
                {
                    return new DownloadTemplateResult
                    {
                        Success = false,
                        Message = validation.Message,
                        IndexItem = item
                    };
                }

                template.ShareCode = string.IsNullOrWhiteSpace(template.ShareCode) ? item.ShareCode : template.ShareCode.Trim().ToUpperInvariant();
                template.GameName = string.IsNullOrWhiteSpace(template.GameName) ? item.GameName : template.GameName;
                template.SteamAppId ??= item.SteamAppId;

                return new DownloadTemplateResult
                {
                    Success = true,
                    Message = I18n.Format("OfficialTemplates_DownloadSuccess", item.DisplayName),
                    LocalPath = cachePath,
                    Template = template,
                    IndexItem = item
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteFile(tempPath);
                return new DownloadTemplateResult
                {
                    Success = false,
                    Message = I18n.Format("OfficialTemplates_DownloadFailed", ex.Message),
                    IndexItem = item
                };
            }
        }

        public static bool TryReadCachedIndex(out IReadOnlyList<RemoteTemplateIndexItem> templates)
        {
            templates = Array.Empty<RemoteTemplateIndexItem>();
            var cachePath = GetIndexCachePath();
            if (!File.Exists(cachePath))
            {
                return false;
            }

            try
            {
                templates = DeserializeIndex(File.ReadAllText(cachePath));
                return templates.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FolderRewind", "1.0"));
            client.Timeout = TimeSpan.FromSeconds(20);
            return client;
        }

        private static IReadOnlyList<RemoteTemplateIndexItem> DeserializeIndex(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<RemoteTemplateIndexItem>();
            }

            var document = JsonSerializer.Deserialize(json, AppJsonContext.Default.RemoteTemplateIndexDocument);
            if (document?.Templates != null && document.Templates.Count > 0)
            {
                return NormalizeIndexItems(document.Templates);
            }

            var list = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListRemoteTemplateIndexItem);
            return NormalizeIndexItems(list ?? new List<RemoteTemplateIndexItem>());
        }

        private static IReadOnlyList<RemoteTemplateIndexItem> NormalizeIndexItems(IEnumerable<RemoteTemplateIndexItem> items)
        {
            var normalized = new List<RemoteTemplateIndexItem>();
            foreach (var item in items)
            {
                if (item == null || !IsValidShareCode(item.ShareCode))
                {
                    continue;
                }

                item.ShareCode = item.ShareCode.Trim().ToUpperInvariant();
                item.TemplateId = item.TemplateId?.Trim() ?? string.Empty;
                item.Name = item.Name?.Trim() ?? string.Empty;
                item.Author = item.Author?.Trim() ?? string.Empty;
                item.Description = item.Description?.Trim() ?? string.Empty;
                item.GameName = item.GameName?.Trim() ?? string.Empty;
                item.BaseConfigType = string.IsNullOrWhiteSpace(item.BaseConfigType) ? "Default" : item.BaseConfigType.Trim();
                item.RequiredPluginIds ??= new ObservableCollection<string>();
                item.FileUrl = item.FileUrl?.Trim() ?? string.Empty;
                item.Sha256 = item.Sha256?.Trim().ToUpperInvariant() ?? string.Empty;
                normalized.Add(item);
            }

            return normalized
                .OrderBy(item => item.IsDisabled)
                .ThenBy(item => item.GameName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string GetOfficialTemplateCacheDirectory()
        {
            return Path.Combine(ConfigService.ConfigDirectory, "cache", "official-templates");
        }

        private static string GetIndexCachePath()
        {
            return Path.Combine(GetOfficialTemplateCacheDirectory(), "index.json");
        }

        private static string GetTemplateCachePath(string shareCode)
        {
            return Path.Combine(GetOfficialTemplateCacheDirectory(), "templates", $"{shareCode.Trim().ToUpperInvariant()}{TemplateService.ShareFileExtension}");
        }

        private static void WriteCachedIndex(string json)
        {
            var path = GetIndexCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var sha256 = SHA256.Create();
            var hash = await sha256.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
