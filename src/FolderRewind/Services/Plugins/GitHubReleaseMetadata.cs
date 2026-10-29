using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FolderRewind.Services.Plugins
{
    internal static partial class GitHubReleaseService
    {
        // Kept independent of transport/WinUI so release fixtures exercise the production parser.
        internal static GitHubLatestReleaseResult ParseMetadata(
            JsonElement root, string requestedUrl, string? responseUrl, string officialUrl, string sourceDisplayName)
        {
            var result = new GitHubLatestReleaseResult
            {
                OfficialMetadata = IsOfficialMetadataSource(requestedUrl, responseUrl, officialUrl),
                IsDraft = ReadBoolean(root, "draft"),
                IsPrerelease = ReadBoolean(root, "prerelease"),
                ReleaseName = ReadString(root, "name"),
                TagName = ReadString(root, "tag_name"),
                Body = ReadString(root, "body"),
                HtmlUrl = ReadString(root, "html_url"),
                ZipballUrl = ReadString(root, "zipball_url"),
                PublishedAt = ReadTimestamp(root, "published_at"),
                SourceDisplayName = sourceDisplayName
            };

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return result;

            var list = new List<GitHubReleaseAsset>();
            foreach (var asset in assets.EnumerateArray())
            {
                var name = ReadString(asset, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Preserve named assets with missing URLs: dropping them could hide duplicate names.
                list.Add(new GitHubReleaseAsset
                {
                    Name = name,
                    DownloadUrl = ReadString(asset, "browser_download_url") ?? string.Empty,
                    SizeBytes = ReadInt64(asset, "size"),
                    Digest = ReadString(asset, "digest") ?? string.Empty,
                    DownloadCount = ReadInt64(asset, "download_count"),
                    UpdatedAt = ReadTimestamp(asset, "updated_at")
                });
            }
            result.Assets = list;
            return result;
        }

        internal static bool IsOfficialMetadataSource(string? requestedUrl, string? responseUrl, string officialUrl)
        {
            return Uri.TryCreate(officialUrl, UriKind.Absolute, out var expected)
                && expected.Host == "api.github.com"
                && GitHubReleaseUrlPolicy.MatchesCanonicalHttpsUrl(requestedUrl, officialUrl)
                && GitHubReleaseUrlPolicy.MatchesCanonicalHttpsUrl(responseUrl, officialUrl);
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool? ReadBoolean(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;

        private static long ReadInt64(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number) ? number : 0;

        private static DateTimeOffset? ReadTimestamp(JsonElement element, string name) =>
            DateTimeOffset.TryParse(ReadString(element, name), out var timestamp) ? timestamp : null;

        public sealed class GitHubLatestReleaseResult
        {
            public bool OfficialMetadata { get; set; }
            public bool? IsDraft { get; set; }
            public bool? IsPrerelease { get; set; }
            public string? ReleaseName { get; set; }
            public string? TagName { get; set; }
            public string? Body { get; set; }
            public string? HtmlUrl { get; set; }
            public string? ZipballUrl { get; set; }
            public DateTimeOffset? PublishedAt { get; set; }
            public IReadOnlyList<GitHubReleaseAsset> Assets { get; set; } = Array.Empty<GitHubReleaseAsset>();
            public string? ErrorMessage { get; set; }
            public string? SourceDisplayName { get; set; }
        }

        public sealed class GitHubReleaseAsset
        {
            public string Digest { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public long DownloadCount { get; set; }
            public DateTimeOffset? UpdatedAt { get; set; }
        }
    }

    internal static class GitHubReleaseUrlPolicy
    {
        internal static bool MatchesCanonicalHttpsUrl(string? raw, string expectedUrl)
        {
            if (raw == null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
            var expected = new Uri(expectedUrl);
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
                || !string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return false;

            // Compare the ORIGINAL authority and path. Uri normalizes dot segments, escapes,
            // whitespace and backslashes; accepting that normalization would hide unsafe input.
            var pathStart = raw.IndexOf('/', "https://".Length);
            if (pathStart < 0) return false;
            var authority = raw[..pathStart];
            var expectedAuthority = "https://" + expected.Host;
            return (string.Equals(authority, expectedAuthority, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(authority, expectedAuthority + ":443", StringComparison.OrdinalIgnoreCase))
                && string.Equals(raw[pathStart..], expected.AbsolutePath, StringComparison.Ordinal);
        }
    }
}
