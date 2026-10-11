using FolderRewind.Services.Plugins;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class AppUpdateService
    {
        private const string Owner = "Leafuke";
        private const string Repo = "FolderRewind";
        private static bool _checkedThisSession;

        public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            if (_checkedThisSession) return null;
            _checkedThisSession = true;

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings != null && !settings.EnableUpdateReminder) return null;

            var latest = await GitHubReleaseService.GetLatestReleaseAsync(Owner, Repo, ct);
            var channel = AppDistributionService.GetCurrentChannel();
            var decision = AppUpdatePolicy.Resolve(latest, AppRuntimeInfo.GetApplicationVersion(),
                channel, RuntimeInformation.OSArchitecture, out var reason);
            try
            {
                LogService.LogInfo($"[AppUpdate] Channel={channel}; Action={decision?.Action.ToString() ?? "-"}; " +
                    $"Arch={decision?.ArchitectureTag ?? "-"}; Package={decision?.AssetName ?? "-"}; " +
                    $"Reason={reason}; OfficialMetadata={latest.OfficialMetadata}", nameof(AppUpdateService));
            }
            catch
            {
                // A diagnostic failure must not suppress a validated update notification.
            }
            if (decision == null) return null;

            return new UpdateCheckResult(decision, latest.ReleaseName,
                ExtractLocalizedReleaseNotes(latest.Body, IsChineseUi()), latest.OfficialMetadata);
        }

        private static bool IsChineseUi()
        {
            try
            {
                var language = I18n.GetCurrentUiLanguage();

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
            internal UpdateCheckResult(AppUpdatePolicy.Decision target, string? releaseName,
                string releaseNotes, bool officialMetadata)
            {
                Target = target;
                ReleaseName = releaseName;
                ReleaseNotes = releaseNotes;
                OfficialMetadata = officialMetadata;
            }

            public AppUpdatePolicy.Decision Target { get; }
            public InstallChannel InstallChannel => Target.InstallChannel;
            public string CurrentVersion => Target.CurrentVersion.ToString(4);
            public string LatestVersion => Target.LatestVersion.ToString(4);
            public string LatestTag => Target.LatestTag;
            public UpdatePrimaryAction PrimaryAction => Target.Action;
            public string? ReleaseName { get; }
            public string ReleaseNotes { get; }
            public bool OfficialMetadata { get; }
            public UpdateDecisionReason FallbackReason => Target.Reason;
            // Removed with the retired sideload executor in the next change.
            public string? PackageAssetName => null;
            public string? PackageDownloadUrl => null;
            public string? PackageSha256Url => null;
        }
    }
}
