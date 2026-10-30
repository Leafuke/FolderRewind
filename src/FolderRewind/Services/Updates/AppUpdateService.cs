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
        private static readonly object Sync = new();
        private static Task? _running;
        internal enum CheckStatus { NotChecked, Checking, UpToDate, Available, Failed }
        internal static CheckStatus Status { get; private set; }
        private static UpdateCheckResult? _latest;
        internal static UpdateCheckResult? Latest
        {
            get => CheckedUtc is { } at && DateTimeOffset.UtcNow - at <= TimeSpan.FromDays(30) ? _latest : null;
            private set => _latest = value;
        }
        internal static GitHubReleaseService.GitHubLatestReleaseResult? Metadata { get; private set; }
        internal static DateTimeOffset? CheckedUtc { get; private set; }
        internal static event Action? Changed;

        public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            await CheckAsync(false, ct);
            return Latest;
        }

        internal static Task CheckAsync(bool manual, CancellationToken ct = default)
        {
            lock (Sync)
            {
                if (_running is { IsCompleted: false }) return _running;
                if (!manual && (_checkedThisSession || !ConfigService.CurrentConfig.GlobalSettings.EnableUpdateReminder))
                    return Task.CompletedTask;
                _checkedThisSession = true;
                return _running = ExecuteCheckAsync(ct);
            }
        }

        private static async Task ExecuteCheckAsync(CancellationToken ct)
        {
            var previous = Status;
            Status = CheckStatus.Checking; Changed?.Invoke();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var release = await GitHubReleaseService.GetLatestReleaseAsync(Owner, Repo, timeout.Token);
                var target = Resolve(release, out var reason);
                if (target is null && reason != UpdateDecisionReason.NotNewer)
                { Status = CheckStatus.Failed; return; }
                Metadata = release; Latest = target;
                CheckedUtc = DateTimeOffset.UtcNow;
                Status = target is null ? CheckStatus.UpToDate : CheckStatus.Available;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { Status = previous; }
            catch (Exception error)
            {
                Status = CheckStatus.Failed;
                LogService.LogError("Application update check failed.", nameof(AppUpdateService), error);
            }
            finally { Changed?.Invoke(); }
        }

        internal static UpdateCheckResult? Resolve(GitHubReleaseService.GitHubLatestReleaseResult release,
            out UpdateDecisionReason reason)
        {
            var decision = AppUpdatePolicy.Resolve(release, AppRuntimeInfo.GetApplicationVersion(),
                AppDistributionService.GetCurrentChannel(), RuntimeInformation.OSArchitecture, out reason);
            return decision is null ? null : new UpdateCheckResult(decision, release.ReleaseName,
                ExtractLocalizedReleaseNotes(release.Body, IsChineseUi()), release.OfficialMetadata);
        }

        internal static void Restore(GitHubReleaseService.GitHubLatestReleaseResult release, DateTimeOffset fetched)
        {
            if (DateTimeOffset.UtcNow - fetched > TimeSpan.FromDays(30) || fetched > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            Latest = Resolve(release, out _); Metadata = release; CheckedUtc = fetched;
            // Cached metadata is readable, but is not a successful check in this session.
            Status = CheckStatus.NotChecked;
        }

        internal static async Task<bool> OpenAsync(bool fallback = false)
        {
            var latest = Metadata is null ? null : Resolve(Metadata, out _);
            if (latest is null) return false;
            var uri = fallback ? new Uri(latest.InstallChannel == InstallChannel.Store
                ? AppUpdatePolicy.StoreProductUrl : AppUpdatePolicy.ReleasesUrl) : latest.Target.Uri;
            try { return await Windows.System.Launcher.LaunchUriAsync(uri); }
            catch (Exception error)
            { LogService.LogError("Could not open the update destination.", nameof(AppUpdateService), error); return false; }
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
        }
    }
}
