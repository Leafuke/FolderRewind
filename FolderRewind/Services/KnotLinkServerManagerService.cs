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
        private const string RegistryUninstallKey =
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\KnotLinkService";
        private const string ServerProcessName = "KnotLinkService";

        private const string GitHubOwner = "KnotLink-Protocol";
        private const string GitHubRepo = "KnotLink";

        /// <summary>
        /// 通过注册表 App Paths 探测 KnotLinkService.exe 的完整路径。
        /// 若注册表不存在或文件缺失则返回 null。
        /// </summary>
        public static string? GetServerExecutablePath()
        {
            var path = RegistryHelper.ReadLocalMachineString(RegistryAppPathsKey);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return path;
        }

        /// <summary>
        /// 通过注册表 Uninstall 键读取 KnotLink 服务端的 DisplayVersion。
        /// </summary>
        public static string? GetServerVersion()
        {
            var version = RegistryHelper.ReadLocalMachineString(RegistryUninstallKey, "DisplayVersion");
            // LogService.LogInfo($"KnotLink server version (registry): {version ?? "not found"}",
            //     nameof(KnotLinkServerManagerService));
            return version;
        }

        /// <summary>
        /// 判断 KnotLink 服务端是否已安装（Uninstall 注册表存在 或 exe 路径存在）。
        /// </summary>
        public static bool IsServerInstalled()
        {
            return GetServerVersion() != null || GetServerExecutablePath() != null;
        }

        /// <summary>
        /// 检测 KnotLinkService 进程是否正在运行。
        /// </summary>
        public static bool IsServerProcessRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName(ServerProcessName);
                return processes.Length > 0;
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });

                LogService.LogInfo($"KnotLink server started: {path}", nameof(KnotLinkServerManagerService));
                return true;
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
        /// 查询 KnotLink-Protocol/KnotLink 的最新 Release。
        /// </summary>
        public static async Task<KnotLinkUpdateInfo?> CheckForServerUpdateAsync(CancellationToken ct = default)
        {
            var currentVersion = GetServerVersion();
            LogService.LogInfo($"Current KnotLink version: {currentVersion ?? "not found"}",
                nameof(KnotLinkServerManagerService));
            if (currentVersion == null) return null;

            var release = await GitHubReleaseService.GetLatestReleaseAsync(GitHubOwner, GitHubRepo, ct);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage) || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            var latestVersion = TryParseTagVersion(release.TagName!);
            if (latestVersion == null) return null;

            var releaseUrl = release.HtmlUrl ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
            var hasUpdate = IsVersionNewer(currentVersion, latestVersion);

            // 从 assets 中查找安装包：匹配 KnotLinkService-X.Y.Z.W-Installer.exe
            string? installerUrl = null;
            string? installerName = null;
            if (release.Assets.Count > 0)
            {
                var installer = release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith("-Installer.exe", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.StartsWith("KnotLinkService", StringComparison.OrdinalIgnoreCase));

                if (installer != null)
                {
                    installerUrl = installer.DownloadUrl;
                    installerName = installer.Name;
                }
            }

            return new KnotLinkUpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                ReleaseUrl = releaseUrl,
                InstallerDownloadUrl = installerUrl,
                InstallerAssetName = installerName,
                HasUpdate = hasUpdate
            };
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
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "KnotLinkService-Installer.exe";

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

        private static bool IsVersionNewer(string currentVersion, string latestVersion)
        {
            var cur = TryParseFileVersion(currentVersion);
            var lat = TryParseFileVersion(latestVersion);
            if (cur == null || lat == null) return false;
            return lat > cur;
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
