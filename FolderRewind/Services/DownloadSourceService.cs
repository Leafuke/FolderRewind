using System;
using System.Collections.Generic;

namespace FolderRewind.Services
{
    internal enum DownloadSourceOption
    {
        Official = 0,
        Mirror1 = 1,
        Mirror2 = 2,
        Custom = 3
    }

    internal sealed class DownloadSourceCandidate
    {
        public required DownloadSourceOption Option { get; init; }
        public required string DisplayName { get; init; }
        public required string Url { get; init; }
    }

    internal sealed class DownloadSourcePairCandidate
    {
        public required DownloadSourceOption Option { get; init; }
        public required string DisplayName { get; init; }
        public required string PrimaryUrl { get; init; }
        public required string SecondaryUrl { get; init; }
    }

    internal static class DownloadSourceService
    {
        private const string Mirror1Prefix = "https://gh-proxy.com/";
        private const string Mirror2Prefix = "https://hk.gh-proxy.org/";

        public static DownloadSourceOption NormalizeSourceOption(int rawValue)
        {
            return Enum.IsDefined(typeof(DownloadSourceOption), rawValue)
                ? (DownloadSourceOption)rawValue
                : DownloadSourceOption.Mirror1;
        }

        public static IReadOnlyList<DownloadSourceCandidate> BuildCandidates(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return Array.Empty<DownloadSourceCandidate>();
            }

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var order = BuildOrderedOptions(
                NormalizeSourceOption(settings?.AppUpdatePreferredSource ?? (int)DownloadSourceOption.Mirror1),
                settings?.AppUpdateAutoFallback ?? true);
            var customMirror = settings?.AppUpdateCustomMirrorUrl ?? string.Empty;

            var candidates = new List<DownloadSourceCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in order)
            {
                var url = RewriteUrl(rawUrl, option, customMirror);
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                {
                    continue;
                }

                candidates.Add(new DownloadSourceCandidate
                {
                    Option = option,
                    DisplayName = GetDisplayName(option),
                    Url = url
                });
            }

            return candidates;
        }

        public static IReadOnlyList<DownloadSourcePairCandidate> BuildPairCandidates(string primaryRawUrl, string secondaryRawUrl)
        {
            if (string.IsNullOrWhiteSpace(primaryRawUrl) || string.IsNullOrWhiteSpace(secondaryRawUrl))
            {
                return Array.Empty<DownloadSourcePairCandidate>();
            }

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var order = BuildOrderedOptions(
                NormalizeSourceOption(settings?.AppUpdatePreferredSource ?? (int)DownloadSourceOption.Mirror1),
                settings?.AppUpdateAutoFallback ?? true);
            var customMirror = settings?.AppUpdateCustomMirrorUrl ?? string.Empty;

            var candidates = new List<DownloadSourcePairCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in order)
            {
                var primaryUrl = RewriteUrl(primaryRawUrl, option, customMirror);
                var secondaryUrl = RewriteUrl(secondaryRawUrl, option, customMirror);
                if (string.IsNullOrWhiteSpace(primaryUrl) || string.IsNullOrWhiteSpace(secondaryUrl))
                {
                    continue;
                }

                var dedupeKey = primaryUrl + "\n" + secondaryUrl;
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                candidates.Add(new DownloadSourcePairCandidate
                {
                    Option = option,
                    DisplayName = GetDisplayName(option),
                    PrimaryUrl = primaryUrl,
                    SecondaryUrl = secondaryUrl
                });
            }

            return candidates;
        }

        public static string RewriteUrl(string rawUrl, DownloadSourceOption option, string? customMirrorOverride = null)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return string.Empty;
            }

            return option switch
            {
                DownloadSourceOption.Official => rawUrl,
                DownloadSourceOption.Mirror1 => RewriteByPrefix(Mirror1Prefix, rawUrl),
                DownloadSourceOption.Mirror2 => RewriteByPrefix(Mirror2Prefix, rawUrl),
                DownloadSourceOption.Custom => RewriteByCustom(customMirrorOverride ?? string.Empty, rawUrl),
                _ => rawUrl
            };
        }

        public static string GetDisplayName(DownloadSourceOption option)
        {
            return option switch
            {
                DownloadSourceOption.Official => I18n.GetString("Update_Source_Official"),
                DownloadSourceOption.Mirror1 => I18n.GetString("Update_Source_Mirror1"),
                DownloadSourceOption.Mirror2 => I18n.GetString("Update_Source_Mirror2"),
                DownloadSourceOption.Custom => I18n.GetString("Update_Source_Custom"),
                _ => I18n.GetString("Update_Source_Official")
            };
        }

        private static IReadOnlyList<DownloadSourceOption> BuildOrderedOptions(DownloadSourceOption preferred, bool autoFallback)
        {
            var order = new List<DownloadSourceOption> { preferred };
            if (!autoFallback)
            {
                return order;
            }

            foreach (DownloadSourceOption option in Enum.GetValues(typeof(DownloadSourceOption)))
            {
                if (option != preferred)
                {
                    order.Add(option);
                }
            }

            return order;
        }

        private static string RewriteByPrefix(string prefix, string rawUrl)
        {
            var normalized = prefix?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return rawUrl;
            }

            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return normalized + rawUrl;
        }

        private static string RewriteByCustom(string customMirror, string rawUrl)
        {
            var normalized = customMirror?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            if (normalized.IndexOf("{url}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return normalized.Replace("{url}", rawUrl, StringComparison.OrdinalIgnoreCase);
            }

            return RewriteByPrefix(normalized, rawUrl);
        }
    }
}
