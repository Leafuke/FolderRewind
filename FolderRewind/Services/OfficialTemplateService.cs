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
    internal static class OfficialBackupPresetService
    {
        internal const string OfficialRepoOwner = "Leafuke";
        internal const string OfficialRepoName = "folderrewind-official-templates";
        internal const string OfficialRepoBranch = "main";

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
            public BackupPreset? Template { get; init; }
            public RemoteTemplateIndexItem? IndexItem { get; init; }
        }

        private sealed class RawIndexResult
        {
            public bool Success { get; init; }
            public IReadOnlyList<RemoteTemplateIndexItem> Items { get; init; } = Array.Empty<RemoteTemplateIndexItem>();
            public string Json { get; init; } = string.Empty;
            public string SourceDisplayName { get; init; } = string.Empty;
            public bool UsedCache { get; init; }
        }

        public static bool IsValidShareCode(string? shareCode)
        {
            return !string.IsNullOrWhiteSpace(shareCode)
                && ShareCodeRegex.IsMatch(shareCode.Trim().ToUpperInvariant());
        }

        public static async Task<FetchIndexResult> GetIndexAsync(bool allowCachedFallback = true, CancellationToken ct = default)
        {
            var v2 = await FetchRawIndexAsync(isV2: true, ct);
            var legacy = await FetchRawIndexAsync(isV2: false, ct);
            if (!v2.Success && allowCachedFallback) v2 = ReadCachedIndex(isV2: true);
            if (!legacy.Success && allowCachedFallback) legacy = ReadCachedIndex(isV2: false);
            if (!v2.Success && !legacy.Success)
            {
                return new FetchIndexResult
                {
                    Success = false,
                    Message = I18n.GetString("OfficialTemplates_FetchIndexFailed")
                };
            }

            var templates = OfficialPresetIndexMergePolicy.MergeV2First(
                v2.Items,
                legacy.Items,
                item => string.IsNullOrWhiteSpace(item.ShareId) ? item.TemplateId : item.ShareId);
            var usedCache = v2.UsedCache || legacy.UsedCache;
            return new FetchIndexResult
            {
                Success = true,
                Templates = templates,
                UsedCache = usedCache,
                SourceDisplayName = string.Join(" + ", new[] { v2.SourceDisplayName, legacy.SourceDisplayName }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)),
                Message = templates.Count == 0
                    ? I18n.GetString("OfficialTemplates_IndexEmpty")
                    : (usedCache ? I18n.GetString("OfficialTemplates_UsingCachedIndex") : string.Empty)
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

            var cachePath = GetTemplateCachePath(item.ShareCode, item.IsV2);
            var tempPath = cachePath + ".tmp";

            string lastError = string.Empty;
            // 模板包下载也走多源候选，和索引策略保持一致。
            foreach (var source in DownloadSourceService.BuildCandidates(item.FileUrl))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    TryDeleteFile(tempPath);

                    using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
                    await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await remoteStream.CopyToAsync(target, ct);
                    }

                    var actualHash = await ComputeFileSha256Async(tempPath, ct);
                    // 官方索引提供哈希时强校验，防止镜像被污染或下载半截文件。
                    if (!string.IsNullOrWhiteSpace(item.Sha256)
                        && !string.Equals(actualHash, item.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = I18n.GetString("OfficialTemplates_HashMismatch");
                        LogService.LogWarning(I18n.Format("OfficialTemplates_FetchIndexFailedLog", source.Url, lastError), nameof(OfficialBackupPresetService));
                        continue;
                    }

                    File.Move(tempPath, cachePath, true);

                    if (!BackupPresetService.TryLoadTemplateFromPackage(cachePath, out var template, out var message) || template == null)
                    {
                        return new DownloadTemplateResult
                        {
                            Success = false,
                            Message = string.IsNullOrWhiteSpace(message) ? I18n.GetString("OfficialTemplates_LoadFailed") : message,
                            IndexItem = item
                        };
                    }

                    // 二次校验：不仅文件能读，还要满足官方共享规则。
                    var validation = BackupPresetService.ValidateTemplateForOfficialSharing(template);
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
                    template.ShareId = string.IsNullOrWhiteSpace(template.ShareId)
                        ? (string.IsNullOrWhiteSpace(item.ShareId) ? item.TemplateId : item.ShareId)
                        : template.ShareId.Trim();
                    template.GameName = string.IsNullOrWhiteSpace(template.GameName) ? item.GameName : template.GameName;
                    template.SteamAppId ??= item.SteamAppId;
                    template.IsRecommended = item.IsRecommended;

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
                    lastError = I18n.Format("OfficialTemplates_DownloadFailed", ex.Message);
                    LogService.LogWarning(I18n.Format("OfficialTemplates_FetchIndexFailedLog", source.Url, ex.Message), nameof(OfficialBackupPresetService));
                }
            }

            return new DownloadTemplateResult
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(lastError) ? I18n.GetString("OfficialTemplates_FetchIndexFailed") : lastError,
                IndexItem = item
            };
        }

        public static bool TryReadCachedIndex(out IReadOnlyList<RemoteTemplateIndexItem> templates)
        {
            var v2 = ReadCachedIndex(isV2: true);
            var legacy = ReadCachedIndex(isV2: false);
            templates = OfficialPresetIndexMergePolicy.MergeV2First(
                v2.Items,
                legacy.Items,
                item => string.IsNullOrWhiteSpace(item.ShareId) ? item.TemplateId : item.ShareId);
            return v2.Success || legacy.Success;
        }

        private static async Task<RawIndexResult> FetchRawIndexAsync(bool isV2, CancellationToken ct)
        {
            foreach (var candidate in BuildIndexCandidates(isV2))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var items = DeserializeIndex(json, isV2);
                    WriteCachedIndex(json, isV2);
                    return new RawIndexResult
                    {
                        Success = true,
                        Items = items,
                        Json = json,
                        SourceDisplayName = candidate.DisplayName
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Log(
                        I18n.Format("OfficialTemplates_FetchIndexFailedLog", candidate.Url, ex.Message),
                        LogLevel.Warning);
                }
            }
            return new RawIndexResult();
        }

        private static RawIndexResult ReadCachedIndex(bool isV2)
        {
            var path = GetIndexCachePath(isV2);
            if (!File.Exists(path)) return new RawIndexResult();
            try
            {
                var json = File.ReadAllText(path);
                return new RawIndexResult
                {
                    Success = true,
                    Items = DeserializeIndex(json, isV2),
                    Json = json,
                    UsedCache = true,
                    SourceDisplayName = I18n.GetString("OfficialTemplates_SourceCache")
                };
            }
            catch
            {
                return new RawIndexResult();
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

        private static IReadOnlyList<RemoteTemplateIndexItem> DeserializeIndex(string json, bool isV2)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<RemoteTemplateIndexItem>();
            }

            using var jsonDocument = JsonDocument.Parse(json);
            if (isV2)
            {
                if (!TemplateFormatPolicy.IsCurrentBackupPresetIndex(jsonDocument.RootElement))
                {
                    throw new JsonException("Unsupported official backup preset index schema.");
                }
                var v2Document = JsonSerializer.Deserialize(json, AppJsonContext.Default.RemoteBackupPresetIndexDocument)
                    ?? throw new JsonException("Invalid official backup preset index.");
                foreach (var item in v2Document.Presets)
                {
                    item.IsV2 = true;
                    item.TemplateId = item.ShareId;
                    item.FileUrl = item.ContentPath;
                }
                return NormalizeIndexItems(v2Document.Presets);
            }
            if (!TemplateFormatPolicy.IsCurrentOfficialIndex(jsonDocument.RootElement))
            {
                throw new JsonException("Unsupported official template index schema.");
            }
            var document = JsonSerializer.Deserialize(json, AppJsonContext.Default.RemoteTemplateIndexDocument)
                ?? throw new JsonException("Invalid official template index.");
            return NormalizeIndexItems(document.Templates);
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

                // 统一清洗字段，后续 UI/导入逻辑就不用到处写 null 与 Trim 防守。
                item.ShareCode = item.ShareCode.Trim().ToUpperInvariant();
                item.ShareId = item.ShareId?.Trim() ?? string.Empty;
                item.TemplateId = item.TemplateId?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(item.ShareId)) item.ShareId = item.TemplateId;
                item.Name = item.Name?.Trim() ?? string.Empty;
                item.Author = item.Author?.Trim() ?? string.Empty;
                item.Description = item.Description?.Trim() ?? string.Empty;
                item.GameName = item.GameName?.Trim() ?? string.Empty;
                item.BaseConfigType = string.IsNullOrWhiteSpace(item.BaseConfigType) ? "Default" : item.BaseConfigType.Trim();
                item.RequiredPluginIds ??= new ObservableCollection<string>();
                item.Matches ??= new ObservableCollection<RemoteBackupPresetMatchKey>();
                item.FileUrl = item.FileUrl?.Trim() ?? string.Empty;
                item.ContentPath = item.ContentPath?.Trim() ?? string.Empty;
                item.Sha256 = item.Sha256?.Trim().ToUpperInvariant() ?? string.Empty;
                item.FileUrl = ResolveTemplateUrl(item.FileUrl, item.ShareCode, item.IsV2);
                normalized.Add(item);
            }

            return normalized
                .OrderBy(item => item.IsDisabled)
                .ThenBy(item => item.GameName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static IReadOnlyList<DownloadSourceCandidate> BuildIndexCandidates(bool isV2)
        {
            var relativePath = isV2 ? "presets/index.json" : "index.json";
            var rawIndexUrl = $"https://raw.githubusercontent.com/{OfficialRepoOwner}/{OfficialRepoName}/{OfficialRepoBranch}/{relativePath}";
            return DownloadSourceService.BuildCandidates(rawIndexUrl);
        }

        private static string ResolveTemplateUrl(string currentUrl, string shareCode, bool isV2)
        {
            if (Uri.TryCreate(currentUrl, UriKind.Absolute, out _))
            {
                return currentUrl;
            }

            var relativePath = isV2
                ? $"presets/{shareCode}.frpreset.json"
                : $"templates/{shareCode}.json";
            return $"https://raw.githubusercontent.com/{OfficialRepoOwner}/{OfficialRepoName}/{OfficialRepoBranch}/{relativePath}";
        }

        private static string GetOfficialTemplateCacheDirectory()
        {
            return Path.Combine(ConfigService.ConfigDirectory, "cache", "official-templates");
        }

        private static string GetIndexCachePath(bool isV2)
        {
            return Path.Combine(GetOfficialTemplateCacheDirectory(), isV2 ? "presets-index.json" : "index.json");
        }

        private static string GetTemplateCachePath(string shareCode, bool isV2 = false)
        {
            var extension = isV2 ? BackupPresetService.ShareFileExtension : BackupPresetService.LegacyShareFileExtension;
            return Path.Combine(GetOfficialTemplateCacheDirectory(), "templates", $"{shareCode.Trim().ToUpperInvariant()}{extension}");
        }

        private static void WriteCachedIndex(string json, bool isV2)
        {
            var path = GetIndexCachePath(isV2);
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
