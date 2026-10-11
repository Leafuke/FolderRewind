using FolderRewind.Services.Plugins;
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FolderRewind.Services
{
    internal enum InstallChannel
    {
        Unknown = 0,
        Store = 1,
        Sideload = 2,
        Developer = 3,
        Msi = 4
    }

    internal enum UpdatePrimaryAction
    {
        OpenReleasePage = 0,
        OpenStorePage = 1,
        PrepareSideloadPackage = 2,
        OpenMsiDownload = 3
    }

    internal enum UpdateDecisionReason
    {
        None,
        InvalidMetadata,
        DraftOrPrerelease,
        NotNewer,
        UnsafeReleaseUrl,
        ManualMigration,
        UnsupportedArchitecture,
        MissingAsset,
        AmbiguousAsset,
        UnsafeAssetUrl
    }

    internal static class AppUpdatePolicy
    {
        internal const string ReleasesUrl = "https://github.com/Leafuke/FolderRewind/releases";
        internal const string StoreProductUrl = "https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct";
        private static readonly Regex ReleaseTag = new(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
            RegexOptions.CultureInvariant);

        internal static Version? ParseReleaseVersion(string? tag)
        {
            if (tag == null || tag.Length > 16) return null;
            var match = ReleaseTag.Match(tag);
            if (!match.Success) return null;
            var parts = new int[3];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
                    return null;
            return parts[0] <= 255 && parts[1] <= 255 && parts[2] <= 65535
                ? new Version(parts[0], parts[1], parts[2], 0) : null;
        }

        internal static Decision? Resolve(GitHubReleaseService.GitHubLatestReleaseResult release,
            Version? currentVersion, InstallChannel channel, Architecture osArchitecture, out UpdateDecisionReason reason)
        {
            reason = UpdateDecisionReason.InvalidMetadata;
            var latest = ParseReleaseVersion(release.TagName);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage) || latest == null || currentVersion == null
                || release.IsDraft == null || release.IsPrerelease == null)
                return null;
            if (release.IsDraft.Value || release.IsPrerelease.Value)
            {
                reason = UpdateDecisionReason.DraftOrPrerelease;
                return null;
            }
            var current = new Version(currentVersion.Major, currentVersion.Minor,
                Math.Max(0, currentVersion.Build), Math.Max(0, currentVersion.Revision));
            if (latest <= current)
            {
                reason = UpdateDecisionReason.NotNewer;
                return null;
            }

            var tag = $"v{latest.Major}.{latest.Minor}.{latest.Build}";
            var releaseUrl = ReleasesUrl + "/tag/" + tag;
            var architecture = osArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => string.Empty
            };
            Decision Target(UpdatePrimaryAction action, string url, UpdateDecisionReason fallback, string? asset = null) =>
                new(channel, current, latest, tag, action, new Uri(url), fallback, architecture, asset);

            // Store must never switch to a GitHub installation, even when GitHub metadata is malformed.
            if (channel == InstallChannel.Store)
            {
                reason = UpdateDecisionReason.None;
                return Target(UpdatePrimaryAction.OpenStorePage, StoreProductUrl, reason);
            }
            if (!GitHubReleaseUrlPolicy.MatchesCanonicalHttpsUrl(release.HtmlUrl, releaseUrl))
            {
                reason = UpdateDecisionReason.UnsafeReleaseUrl;
                return Target(UpdatePrimaryAction.OpenReleasePage, ReleasesUrl, reason);
            }
            if (channel != InstallChannel.Msi)
            {
                reason = UpdateDecisionReason.ManualMigration;
                return Target(UpdatePrimaryAction.OpenReleasePage, releaseUrl, reason);
            }
            if (architecture.Length == 0)
            {
                reason = UpdateDecisionReason.UnsupportedArchitecture;
                return Target(UpdatePrimaryAction.OpenReleasePage, ReleasesUrl, reason);
            }

            var assetName = $"FolderRewind_{latest.ToString(4)}_Setup_{architecture}.exe";
            var matches = release.Assets.Where(asset => string.Equals(asset.Name, assetName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                reason = matches.Length == 0 ? UpdateDecisionReason.MissingAsset : UpdateDecisionReason.AmbiguousAsset;
                return Target(UpdatePrimaryAction.OpenReleasePage, ReleasesUrl, reason);
            }
            var downloadUrl = ReleasesUrl + "/download/" + tag + "/" + assetName;
            if (!GitHubReleaseUrlPolicy.MatchesCanonicalHttpsUrl(matches[0].DownloadUrl, downloadUrl))
            {
                reason = UpdateDecisionReason.UnsafeAssetUrl;
                return Target(UpdatePrimaryAction.OpenReleasePage, ReleasesUrl, reason);
            }

            reason = UpdateDecisionReason.None;
            return Target(UpdatePrimaryAction.OpenMsiDownload, downloadUrl, reason, assetName);
        }

        internal sealed class Decision
        {
            internal Decision(InstallChannel channel, Version current, Version latest, string tag,
                UpdatePrimaryAction action, Uri uri, UpdateDecisionReason reason, string architecture, string? assetName)
            {
                InstallChannel = channel;
                CurrentVersion = current;
                LatestVersion = latest;
                LatestTag = tag;
                Action = action;
                Uri = uri;
                Reason = reason;
                ArchitectureTag = architecture;
                AssetName = assetName;
            }

            internal InstallChannel InstallChannel { get; }
            internal Version CurrentVersion { get; }
            internal Version LatestVersion { get; }
            internal string LatestTag { get; }
            internal UpdatePrimaryAction Action { get; }
            internal Uri Uri { get; }
            internal UpdateDecisionReason Reason { get; }
            internal string ArchitectureTag { get; }
            internal string? AssetName { get; }
        }
    }
}
