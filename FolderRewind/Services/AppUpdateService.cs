using FolderRewind.Services.Plugins;
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;

namespace FolderRewind.Services
{
    internal static class AppUpdateService
    {
        private const string Owner = "Leafuke";
        private const string Repo = "FolderRewind";
        private const string ReleasesUrl = "https://github.com/Leafuke/FolderRewind/releases";

        private static bool _checkedThisSession;

        public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            if (_checkedThisSession) return null;
            _checkedThisSession = true;

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings != null && !settings.EnableUpdateReminder) return null;

            var latest = await GitHubReleaseService.GetLatestReleaseAsync(Owner, Repo, ct);
            if (!string.IsNullOrWhiteSpace(latest.ErrorMessage)) return null;
            if (string.IsNullOrWhiteSpace(latest.TagName)) return null;

            var latestVersion = TryParseVersion(latest.TagName);
            var currentVersion = GetCurrentVersion();

            if (latestVersion == null || currentVersion == null) return null;
            if (latestVersion <= currentVersion) return null;

            var isChinese = IsChineseUi();
            var notes = ExtractLocalizedReleaseNotes(latest.Body, isChinese);

            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion.ToString(4),
                LatestVersion = latestVersion.ToString(4),
                LatestTag = latest.TagName,
                ReleaseName = latest.ReleaseName,
                ReleaseUrl = string.IsNullOrWhiteSpace(latest.HtmlUrl) ? ReleasesUrl : latest.HtmlUrl!,
                ReleaseNotes = notes
            };
        }

        private static Version? GetCurrentVersion()
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return new Version(v.Major, v.Minor, v.Build, v.Revision);
            }
            catch
            {
                try
                {
                    var v = typeof(AppUpdateService).Assembly.GetName().Version;
                    return v == null ? null : NormalizeVersion(v);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static Version? TryParseVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var match = Regex.Match(raw, @"\d+(?:\.\d+){0,3}");
            if (!match.Success) return null;

            var parts = match.Value.Split('.');
            var normalized = new int[4];

            for (int i = 0; i < normalized.Length; i++)
            {
                normalized[i] = 0;
                if (i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                {
                    normalized[i] = Math.Max(0, p);
                }
            }

            return new Version(normalized[0], normalized[1], normalized[2], normalized[3]);
        }

        private static Version NormalizeVersion(Version version)
        {
            var major = Math.Max(0, version.Major);
            var minor = Math.Max(0, version.Minor);
            var build = version.Build < 0 ? 0 : version.Build;
            var revision = version.Revision < 0 ? 0 : version.Revision;

            return new Version(major, minor, build, revision);
        }

        private static bool IsChineseUi()
        {
            try
            {
                var language = ApplicationLanguages.PrimaryLanguageOverride;
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = CultureInfo.CurrentUICulture.Name;
                }

                return !string.IsNullOrWhiteSpace(language)
                    && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static string ExtractLocalizedReleaseNotes(string? body, bool isChinese)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;

            var separator = body.IndexOf("\n---\n", StringComparison.Ordinal);
            if (separator < 0)
            {
                separator = body.IndexOf("\r\n---\r\n", StringComparison.Ordinal);
            }

            if (separator < 0)
            {
                return body.Trim();
            }

            var upper = body[..separator].Trim();
            var lowerStart = body.IndexOf('\n', separator + 1);
            var lower = lowerStart >= 0 ? body[(lowerStart + 1)..].Trim() : string.Empty;

            return isChinese ? upper : lower;
        }

        internal sealed class UpdateCheckResult
        {
            public string CurrentVersion { get; set; } = string.Empty;
            public string LatestVersion { get; set; } = string.Empty;
            public string LatestTag { get; set; } = string.Empty;
            public string? ReleaseName { get; set; }
            public string ReleaseUrl { get; set; } = ReleasesUrl;
            public string ReleaseNotes { get; set; } = string.Empty;
        }
    }
}
