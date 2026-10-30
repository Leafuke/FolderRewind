using FolderRewind.Services.Plugins;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// KnotLink 服务端管理服务：路径探测、版本读取、进程管理、更新检测
    /// </summary>
    internal static class KnotLinkServerManagerService
    {
        private const string RegistryAppPathsKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\KnotLinkService.exe";
        private const string ServerProcessName = "KnotLinkService";

        private const string GitHubOwner = "KnotLink-Protocol";
        private const string GitHubRepo = "KnotLinkService";
        private const string GitHubReleasesUrl = "https://github.com/KnotLink-Protocol/KnotLinkService/releases";
        private const string DefaultInstallerFileName = "KnotLinkService-windows-x86-Installer.exe";
        private static readonly Version MinimumSupportedServerVersionValue = KnotLinkOnboardingWorkflow.MinimumSupportedVersion;

        public static Version MinimumSupportedServerVersion => MinimumSupportedServerVersionValue;

        public static string OfficialReleasesUrl => GitHubReleasesUrl;

        public static string? GetServerExecutablePath() => InspectInstallation().ExecutablePath;
        public static string? GetServerVersion() => InspectInstallation().DisplayVersion;
        public static bool IsServerInstalled()
        {
            var installed = InspectInstallation();
            return installed.ExecutablePath is not null || installed.DisplayVersion is not null;
        }

        internal static KnotLinkInstallation InspectInstallation()
        {
            KnotLinkInstallation? found = null;
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var appPath = root.OpenSubKey(RegistryAppPathsKey);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KnotLinkService");
                    var path = (appPath?.GetValue(null) as string)?.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(path) && uninstall?.GetValue("InstallLocation") is string location)
                        path = Path.Combine(location.Trim().Trim('"'), "KnotLinkService.exe");
                    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)
                        || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) path = null;
                    var display = uninstall?.GetValue("DisplayVersion") as string;
                    var parsed = string.IsNullOrWhiteSpace(display) ? null : TryParseFileVersion(display);
                    if (parsed is null && path is not null)
                    {
                        display = FileVersionInfo.GetVersionInfo(path).FileVersion;
                        parsed = string.IsNullOrWhiteSpace(display) ? null : TryParseFileVersion(display);
                    }
                    var candidate = new KnotLinkInstallation(path, display, parsed);
                    if (candidate.Compatible) return candidate;
                    if (found is null && (path is not null || display is not null)) found = candidate;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                    or ArgumentException or System.ComponentModel.Win32Exception) { }
            }
            return found ?? new(null, null, null);
        }

        public static KnotLinkServerCompatibilityInfo GetServerCompatibilityInfo()
        {
            var installation = InspectInstallation();
            var isInstalled = installation.ExecutablePath is not null || installation.DisplayVersion is not null;
            return new KnotLinkServerCompatibilityInfo
            {
                IsInstalled = isInstalled,
                CurrentVersion = installation.DisplayVersion,
                ParsedVersion = installation.Version,
                RequiresUpdate = isInstalled && !installation.Compatible
            };
        }

        /// <summary>
        /// 检测 KnotLinkService 进程是否正在运行。
        /// </summary>
        public static bool IsServerProcessRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName(ServerProcessName);
                try { return processes.Length > 0; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 启动 KnotLinkService.exe。成功返回 true。
        /// </summary>
        public static bool TryStartServer()
        {
            var path = GetServerExecutablePath();
            if (path == null) return false;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                LogService.LogInfo($"KnotLink server started: {path}", nameof(KnotLinkServerManagerService));
                return process is not null;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Failed to start KnotLink server: {ex.Message}",
                    nameof(KnotLinkServerManagerService));
                return false;
            }
        }

        /// <summary>
        /// 等待 KnotLink 2.x 的发送器和响应器端口可连接。
        /// </summary>
        public static async Task<bool> WaitForServerReadyAsync(
            string host = "127.0.0.1",
            int timeoutMs = 10000,
            CancellationToken ct = default)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            while (!timeoutCts.IsCancellationRequested)
            {
                if (await CanConnectAsync(host, 6370, timeoutCts.Token).ConfigureAwait(false)
                    && await CanConnectAsync(host, 6378, timeoutCts.Token).ConfigureAwait(false))
                {
                    return true;
                }

                try
                {
                    await Task.Delay(200, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return false;
        }

        private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检测 KnotLink 服务端是否有新版本可用。
        /// 查询 KnotLink-Protocol/KnotLinkService 的最新 Release。
        /// </summary>
        public static async Task<KnotLinkUpdateInfo?> CheckForServerUpdateAsync(CancellationToken ct = default)
        {
            var compatibility = GetServerCompatibilityInfo();
            var currentVersion = compatibility.CurrentVersion;
            LogService.LogInfo($"Current KnotLink version: {currentVersion ?? "not found"}",
                nameof(KnotLinkServerManagerService));

            var release = await GitHubReleaseService.GetLatestReleaseAsync(GitHubOwner, GitHubRepo, ct);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage) || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            var latestVersion = TryParseTagVersion(release.TagName!);
            if (latestVersion == null) return null;

            var releaseUrl = release.HtmlUrl ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
            var hasUpdate = !compatibility.IsInstalled
                || compatibility.ParsedVersion == null
                || IsVersionNewer(compatibility.ParsedVersion, latestVersion);

            // 仅匹配新的 Windows x86 安装包命名：
            // KnotLinkService-X.Y.Z.W-windows-x86-Installer.exe
            string? installerUrl = null;
            string? installerName = null;
            if (release.Assets.Count > 0)
            {
                var installer = release.Assets.FirstOrDefault(a =>
                    KnotLinkReleaseAssetPolicy.IsWindowsX86Installer(a.Name));

                if (installer != null)
                {
                    installerUrl = installer.DownloadUrl;
                    installerName = installer.Name;
                }
            }

            return new KnotLinkUpdateInfo
            {
                CurrentVersion = currentVersion ?? string.Empty,
                LatestVersion = latestVersion,
                ReleaseUrl = releaseUrl,
                InstallerDownloadUrl = installerUrl,
                InstallerAssetName = installerName,
                HasUpdate = hasUpdate
            };
        }

        /// <summary>
        /// 使用与设置页相同的更新链路下载并启动最新兼容安装器。
        /// 未提供更新信息时会先查询官方最新 Release。
        /// </summary>
        public static async Task<KnotLinkUpdateInfo> DownloadAndLaunchLatestInstallerAsync(
            KnotLinkUpdateInfo? updateInfo = null,
            CancellationToken ct = default)
        {
            updateInfo ??= await CheckForServerUpdateAsync(ct).ConfigureAwait(false);
            if (updateInfo == null)
            {
                throw new InvalidOperationException(
                    I18n.GetString("KnotLinkCompatibility_UpdateInfoUnavailable"));
            }

            var latestVersion = TryParseFileVersion(updateInfo.LatestVersion);
            if (latestVersion == null || latestVersion < MinimumSupportedServerVersionValue)
            {
                throw new InvalidOperationException(I18n.Format(
                    "KnotLinkCompatibility_LatestVersionUnsupported",
                    updateInfo.LatestVersion,
                    FormatVersion(MinimumSupportedServerVersionValue)));
            }

            if (string.IsNullOrWhiteSpace(updateInfo.InstallerDownloadUrl))
            {
                throw new InvalidOperationException(
                    I18n.GetString("SettingsPage_KnotLinkServerNoInstaller"));
            }

            var localPath = await DownloadInstallerAsync(
                updateInfo.InstallerDownloadUrl,
                ct).ConfigureAwait(false);
            if (localPath == null)
            {
                throw new InvalidOperationException(
                    I18n.GetString("KnotLinkCompatibility_DownloadFailed"));
            }

            LaunchInstaller(localPath);
            return updateInfo;
        }

        /// <summary>
        /// 下载 KnotLink 安装包到临时目录，返回本地文件路径。
        /// </summary>
        public static async Task<string?> DownloadInstallerAsync(string downloadUrl, CancellationToken ct = default)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "FolderRewind", "KnotLinkUpdate");
                Directory.CreateDirectory(tempDir);

                var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = DefaultInstallerFileName;

                var localPath = Path.Combine(tempDir, fileName);
                var bytes = await GitHubReleaseService.DownloadAssetAsync(downloadUrl, ct);
                await File.WriteAllBytesAsync(localPath, bytes, ct);
                return localPath;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Failed to download KnotLink installer: {ex.Message}",
                    nameof(KnotLinkServerManagerService));
                return null;
            }
        }

        /// <summary>
        /// 启动已下载的安装包。
        /// </summary>
        public static void LaunchInstaller(string installerPath)
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "open"
            });

            LogService.LogInfo($"KnotLink installer launched: {installerPath}",
                nameof(KnotLinkServerManagerService));
        }

        private static bool IsVersionNewer(Version currentVersion, string latestVersion)
        {
            var lat = TryParseFileVersion(latestVersion);
            return lat != null && lat > currentVersion;
        }

        private static string? TryParseTagVersion(string tagName)
        {
            var match = Regex.Match(tagName, @"(\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static Version? TryParseFileVersion(string version)
        {
            var match = Regex.Match(version, @"(\d+(?:\.\d+){0,3})");
            if (!match.Success) return null;

            var parts = match.Groups[1].Value.Split('.');
            var normalized = new int[4];
            for (int i = 0; i < 4; i++)
            {
                normalized[i] = i < parts.Length && int.TryParse(parts[i], out var p) ? Math.Max(0, p) : 0;
            }

            return new Version(normalized[0], normalized[1], normalized[2], normalized[3]);
        }

        private static string FormatVersion(Version version) =>
            $"{version.Major}.{version.Minor}";
    }

    internal sealed class KnotLinkServerCompatibilityInfo
    {
        public bool IsInstalled { get; init; }
        public string? CurrentVersion { get; init; }
        public Version? ParsedVersion { get; init; }
        public bool RequiresUpdate { get; init; }
    }

    /// <summary>
    /// KnotLink 服务端更新检测结果
    /// </summary>
    public sealed class KnotLinkUpdateInfo
    {
        public string CurrentVersion { get; init; } = string.Empty;
        public string LatestVersion { get; init; } = string.Empty;
        public string ReleaseUrl { get; init; } = string.Empty;
        public string? InstallerDownloadUrl { get; init; }
        public string? InstallerAssetName { get; init; }
        public bool HasUpdate { get; init; }
    }
}
