using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;

namespace FolderRewind.Services
{
    internal enum UpdatePrimaryAction
    {
        OpenReleasePage = 0,
        OpenStorePage = 1,
        PrepareSideloadPackage = 2
    }

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

            var channel = AppDistributionService.GetCurrentChannel();
            var primaryAction = UpdatePrimaryAction.OpenReleasePage;
            var primaryActionUrl = string.IsNullOrWhiteSpace(latest.HtmlUrl) ? ReleasesUrl : latest.HtmlUrl!;
            string? packageAssetName = null;
            string? packageDownloadUrl = null;
            string? packageSha256AssetName = null;
            string? packageSha256Url = null;
            string architectureTag = string.Empty;

            if (channel == InstallChannel.Store)
            {
                primaryAction = UpdatePrimaryAction.OpenStorePage;
                primaryActionUrl = AppDistributionService.MicrosoftStoreProductUrl;
            }
            else
            {
                var (packageAsset, sha256Asset, resolvedArchitectureTag) = SelectSideloadPackageAssets(latest.Assets);
                architectureTag = resolvedArchitectureTag;
                if (packageAsset != null && sha256Asset != null)
                {
                    primaryAction = UpdatePrimaryAction.PrepareSideloadPackage;
                    packageAssetName = packageAsset.Name;
                    packageDownloadUrl = packageAsset.DownloadUrl;
                    packageSha256AssetName = sha256Asset.Name;
                    packageSha256Url = sha256Asset.DownloadUrl;
                }
            }

            LogUpdateActionDecision(
                channel,
                primaryAction,
                architectureTag,
                packageAssetName,
                packageSha256AssetName);

            var isChinese = IsChineseUi();
            var notes = ExtractLocalizedReleaseNotes(latest.Body, isChinese);

            return new UpdateCheckResult
            {
                InstallChannel = channel,
                CurrentVersion = currentVersion.ToString(4),
                LatestVersion = latestVersion.ToString(4),
                LatestTag = latest.TagName,
                ReleaseName = latest.ReleaseName,
                ReleaseUrl = string.IsNullOrWhiteSpace(latest.HtmlUrl) ? ReleasesUrl : latest.HtmlUrl!,
                ReleaseNotes = notes,
                PrimaryAction = primaryAction,
                PrimaryActionUrl = primaryActionUrl,
                PackageAssetName = packageAssetName,
                PackageDownloadUrl = packageDownloadUrl,
                PackageSha256AssetName = packageSha256AssetName,
                PackageSha256Url = packageSha256Url,
                ArchitectureTag = architectureTag
            };
        }

        private static (GitHubReleaseService.GitHubReleaseAsset? PackageAsset, GitHubReleaseService.GitHubReleaseAsset? Sha256Asset, string ArchitectureTag) SelectSideloadPackageAssets(IReadOnlyList<GitHubReleaseService.GitHubReleaseAsset> assets)
        {
            var architectureTag = GetCurrentArchitectureTag();
            if (assets == null || assets.Count == 0)
            {
                return (null, null, architectureTag);
            }

            var packageCandidates = assets
                .Where(a => a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (packageCandidates.Count == 0)
            {
                return (null, null, architectureTag);
            }

            var packageAsset = SelectPackageAssetByArchitecture(packageCandidates, architectureTag);
            if (packageAsset == null)
            {
                return (null, null, architectureTag);
            }

            var sha256Asset = FindSha256Asset(packageAsset, assets);
            return (packageAsset, sha256Asset, architectureTag);
        }

        private static GitHubReleaseService.GitHubReleaseAsset? SelectPackageAssetByArchitecture(IReadOnlyList<GitHubReleaseService.GitHubReleaseAsset> packageCandidates, string architectureTag)
        {
            if (packageCandidates == null || packageCandidates.Count == 0)
            {
                return null;
            }

            var suffix = $"_{architectureTag}.7z";
            var exactSuffixMatch = packageCandidates
                .FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (exactSuffixMatch != null)
            {
                return exactSuffixMatch;
            }

            var architectureTokenMatch = packageCandidates
                .FirstOrDefault(a => a.Name.Contains($"_{architectureTag}_", StringComparison.OrdinalIgnoreCase));
            if (architectureTokenMatch != null)
            {
                return architectureTokenMatch;
            }

            // 部分发行不会提供 x86 包，允许 x86 设备回退到 x64 入口。
            if (string.Equals(architectureTag, "x86", StringComparison.OrdinalIgnoreCase))
            {
                var x64Fallback = packageCandidates
                    .FirstOrDefault(a => a.Name.EndsWith("_x64.7z", StringComparison.OrdinalIgnoreCase));
                if (x64Fallback != null)
                {
                    return x64Fallback;
                }
            }

            return packageCandidates.Count == 1 ? packageCandidates[0] : null;
        }

        private static GitHubReleaseService.GitHubReleaseAsset? FindSha256Asset(
            GitHubReleaseService.GitHubReleaseAsset packageAsset,
            IReadOnlyList<GitHubReleaseService.GitHubReleaseAsset> allAssets)
        {
            var baseName = Path.GetFileNameWithoutExtension(packageAsset.Name);

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                packageAsset.Name + ".sha256",
                packageAsset.Name + ".sha256.txt",
                baseName + ".sha256",
                baseName + ".sha256.txt"
            };

            var exactMatch = allAssets.FirstOrDefault(a => candidates.Contains(a.Name));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            var shaAssets = allAssets
                .Where(a => IsSha256AssetName(a.Name))
                .ToList();

            if (shaAssets.Count == 0)
            {
                return null;
            }

            var baseNameMatch = shaAssets.FirstOrDefault(a =>
                a.Name.Contains(baseName, StringComparison.OrdinalIgnoreCase));
            if (baseNameMatch != null)
            {
                return baseNameMatch;
            }

            var architectureTag = GetArchitectureTagFromName(packageAsset.Name);
            if (!string.IsNullOrWhiteSpace(architectureTag))
            {
                var architectureMatch = shaAssets.FirstOrDefault(a =>
                    a.Name.Contains($"_{architectureTag}", StringComparison.OrdinalIgnoreCase));
                if (architectureMatch != null)
                {
                    return architectureMatch;
                }
            }

            return shaAssets.Count == 1 ? shaAssets[0] : null;
        }

        private static bool IsSha256AssetName(string name)
        {
            return name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".sha256.txt", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetArchitectureTagFromName(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return string.Empty;
            }

            if (assetName.Contains("_arm64", StringComparison.OrdinalIgnoreCase))
            {
                return "arm64";
            }

            if (assetName.Contains("_x64", StringComparison.OrdinalIgnoreCase))
            {
                return "x64";
            }

            if (assetName.Contains("_x86", StringComparison.OrdinalIgnoreCase))
            {
                return "x86";
            }

            if (assetName.Contains("_arm", StringComparison.OrdinalIgnoreCase))
            {
                return "arm";
            }

            return string.Empty;
        }

        private static void LogUpdateActionDecision(
            InstallChannel channel,
            UpdatePrimaryAction action,
            string architectureTag,
            string? packageAssetName,
            string? sha256AssetName)
        {
            try
            {
                var message = $"[AppUpdate] Channel={channel}; Action={action}; Arch={architectureTag}; Package={packageAssetName ?? "-"}; Sha256={sha256AssetName ?? "-"}";
                LogService.LogInfo(message, nameof(AppUpdateService));
            }
            catch
            {
            }
        }

        private static string GetCurrentArchitectureTag()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => "x64"
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
            public InstallChannel InstallChannel { get; set; } = InstallChannel.Unknown;
            public string CurrentVersion { get; set; } = string.Empty;
            public string LatestVersion { get; set; } = string.Empty;
            public string LatestTag { get; set; } = string.Empty;
            public string? ReleaseName { get; set; }
            public string ReleaseUrl { get; set; } = ReleasesUrl;
            public string ReleaseNotes { get; set; } = string.Empty;
            public UpdatePrimaryAction PrimaryAction { get; set; } = UpdatePrimaryAction.OpenReleasePage;
            public string PrimaryActionUrl { get; set; } = ReleasesUrl;
            public string? PackageAssetName { get; set; }
            public string? PackageDownloadUrl { get; set; }
            public string? PackageSha256AssetName { get; set; }
            public string? PackageSha256Url { get; set; }
            public string ArchitectureTag { get; set; } = string.Empty;
        }
    }
}
