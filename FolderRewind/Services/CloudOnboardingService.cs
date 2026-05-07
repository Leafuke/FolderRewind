using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class CloudOnboardingService
    {
        private const string RcloneOwner = "rclone";
        private const string RcloneRepo = "rclone";
        private const string OpenListOwner = "OpenListTeam";
        private const string OpenListRepo = "OpenList";
        private const string CloudGuideUrl = "https://folderrewind.top/docs/guides/cloud-archive";

        public static IReadOnlyList<CloudOnboardingProviderOption> GetProviderOptions()
        {
            return new[]
            {
                CreateOption("webdav", "CloudOnboarding_Provider_WebDav", "CloudOnboarding_Provider_WebDavDesc", false, "fr_webdav:FolderRewind"),
                CreateOption("onedrive", "CloudOnboarding_Provider_OneDrive", "CloudOnboarding_Provider_OneDriveDesc", false, "fr_onedrive:FolderRewind"),
                CreateOption("s3", "CloudOnboarding_Provider_S3", "CloudOnboarding_Provider_S3Desc", false, "fr_s3:FolderRewind"),
                CreateOption("baidu", "CloudOnboarding_Provider_Baidu", "CloudOnboarding_Provider_BaiduDesc", true, "fr_baidu:/baidu/FolderRewind"),
                CreateOption("aliyun", "CloudOnboarding_Provider_Aliyun", "CloudOnboarding_Provider_AliyunDesc", true, "fr_aliyun:/aliyun/FolderRewind"),
                CreateOption("openlist", "CloudOnboarding_Provider_OpenList", "CloudOnboarding_Provider_OpenListDesc", true, "fr_openlist:/FolderRewind"),
            };
        }

        public static async Task<CloudOnboardingResult> InstallPresetAsync(
            CloudOnboardingProviderOption? provider,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (provider == null)
            {
                var message = I18n.GetString("CloudOnboarding_NoProvider");
                NotificationService.ShowWarning(message, I18n.GetString("CloudOnboarding_Title"));
                return new CloudOnboardingResult { Success = false, Message = message };
            }

            try
            {
                progress?.Report(I18n.GetString("CloudOnboarding_Status_Start"));

                var rclonePath = await EnsureToolAsync(
                    RcloneOwner,
                    RcloneRepo,
                    "rclone",
                    "rclone.exe",
                    SelectRcloneAsset,
                    I18n.GetString("CloudOnboarding_Status_DownloadRclone"),
                    progress,
                    ct).ConfigureAwait(false);

                string openListPath = string.Empty;
                if (provider.RequiresOpenList)
                {
                    openListPath = await EnsureToolAsync(
                        OpenListOwner,
                        OpenListRepo,
                        "openlist",
                        "openlist.exe",
                        SelectOpenListAsset,
                        I18n.GetString("CloudOnboarding_Status_DownloadOpenList"),
                        progress,
                        ct).ConfigureAwait(false);
                }

                progress?.Report(I18n.GetString("CloudOnboarding_Status_SaveSettings"));
                ApplyRclonePath(rclonePath, provider.SuggestedRemoteBasePath);

                OpenGuidance(rclonePath, openListPath);

                var message = provider.RequiresOpenList
                    ? I18n.Format("CloudOnboarding_Success_WithOpenList", provider.DisplayName, rclonePath, openListPath, provider.SuggestedRemoteBasePath)
                    : I18n.Format("CloudOnboarding_Success_RcloneOnly", provider.DisplayName, rclonePath, provider.SuggestedRemoteBasePath);

                LogService.LogInfo(I18n.Format("CloudOnboarding_Log_Success", provider.Id, rclonePath, openListPath), nameof(CloudOnboardingService));
                NotificationService.ShowSuccess(message, I18n.GetString("CloudOnboarding_Title"), 10000);

                return new CloudOnboardingResult
                {
                    Success = true,
                    Message = message,
                    RcloneExecutablePath = rclonePath,
                    OpenListExecutablePath = openListPath
                };
            }
            catch (OperationCanceledException)
            {
                var message = I18n.GetString("Common_Canceled");
                LogService.LogWarning(I18n.GetString("CloudOnboarding_Canceled_Log"), nameof(CloudOnboardingService));
                NotificationService.ShowWarning(message, I18n.GetString("CloudOnboarding_Title"));
                return new CloudOnboardingResult { Success = false, Message = message };
            }
            catch (Exception ex)
            {
                var message = I18n.Format("CloudOnboarding_Failed", ex.Message);
                LogService.LogError(message, nameof(CloudOnboardingService), ex);
                NotificationService.ShowError(message, I18n.GetString("CloudOnboarding_Title"));
                return new CloudOnboardingResult { Success = false, Message = message };
            }
        }

        private static CloudOnboardingProviderOption CreateOption(
            string id,
            string nameKey,
            string descriptionKey,
            bool requiresOpenList,
            string suggestedRemoteBasePath)
        {
            return new CloudOnboardingProviderOption
            {
                Id = id,
                DisplayName = I18n.GetString(nameKey),
                Description = I18n.GetString(descriptionKey),
                RequiresOpenList = requiresOpenList,
                SuggestedRemoteBasePath = suggestedRemoteBasePath
            };
        }

        private static async Task<string> EnsureToolAsync(
            string owner,
            string repo,
            string toolDirectoryName,
            string executableFileName,
            Func<IReadOnlyList<GitHubReleaseService.GitHubReleaseAsset>, GitHubReleaseService.GitHubReleaseAsset?> assetSelector,
            string downloadStatus,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            progress?.Report(downloadStatus);

            var release = await GitHubReleaseService.GetLatestReleaseAsync(owner, repo, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage))
            {
                throw new InvalidOperationException(release.ErrorMessage);
            }

            var tagName = string.IsNullOrWhiteSpace(release.TagName) ? "latest" : SanitizePathSegment(release.TagName);
            var installDir = Path.Combine(ConfigService.ConfigDirectory, "tools", toolDirectoryName, tagName);
            var existingExecutable = FindExecutable(installDir, executableFileName);
            if (!string.IsNullOrWhiteSpace(existingExecutable))
            {
                LogService.LogInfo(I18n.Format("CloudOnboarding_Log_ReuseTool", toolDirectoryName, existingExecutable), nameof(CloudOnboardingService));
                return existingExecutable;
            }

            var asset = assetSelector(release.Assets)
                ?? throw new InvalidOperationException(I18n.Format("CloudOnboarding_AssetMissing", repo));

            var downloadDir = Path.Combine(ConfigService.ConfigDirectory, "tools", "_downloads");
            Directory.CreateDirectory(downloadDir);
            Directory.CreateDirectory(installDir);

            var zipPath = Path.Combine(downloadDir, $"{Guid.NewGuid():N}-{asset.Name}");
            try
            {
                var bytes = await GitHubReleaseService.DownloadAssetAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(zipPath, bytes, ct).ConfigureAwait(false);

                // Release 包通常自带一层版本目录。递归查找 exe，避免把资产内部结构写死。
                ZipFile.ExtractToDirectory(zipPath, installDir, overwriteFiles: true);
                var executable = FindExecutable(installDir, executableFileName);
                if (string.IsNullOrWhiteSpace(executable))
                {
                    throw new FileNotFoundException(I18n.Format("CloudOnboarding_ExecutableMissing", executableFileName), installDir);
                }

                LogService.LogInfo(I18n.Format("CloudOnboarding_Log_ToolInstalled", toolDirectoryName, release.TagName ?? "latest", executable), nameof(CloudOnboardingService));
                return executable;
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        private static GitHubReleaseService.GitHubReleaseAsset? SelectRcloneAsset(IReadOnlyList<GitHubReleaseService.GitHubReleaseAsset> assets)
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "windows-arm64" : "windows-amd64";
            return assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains(arch, StringComparison.OrdinalIgnoreCase));
        }

        private static GitHubReleaseService.GitHubReleaseAsset? SelectOpenListAsset(IReadOnlyList<GitHubReleaseService.GitHubReleaseAsset> assets)
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "windows-arm64" : "windows-amd64";
            var candidates = assets
                .Where(asset =>
                    asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && asset.Name.Contains(arch, StringComparison.OrdinalIgnoreCase)
                    && !asset.Name.Contains("windows7", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return candidates.FirstOrDefault(asset => !asset.Name.Contains("lite", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
        }

        private static string FindExecutable(string directory, string executableFileName)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return string.Empty;
            }

            return Directory
                .EnumerateFiles(directory, executableFileName, SearchOption.AllDirectories)
                .FirstOrDefault() ?? string.Empty;
        }

        private static void ApplyRclonePath(string rclonePath, string suggestedRemoteBasePath)
        {
            var settings = ConfigService.CurrentConfig.GlobalSettings;
            settings.RcloneExecutablePath = rclonePath;
            if (!string.IsNullOrWhiteSpace(suggestedRemoteBasePath)
                && string.Equals(settings.DefaultCloudRemoteBasePath, "remote:FolderRewind", StringComparison.OrdinalIgnoreCase))
            {
                // 只在默认占位值时替换，避免覆盖用户已有 remote 名称。
                settings.DefaultCloudRemoteBasePath = suggestedRemoteBasePath;
            }

            ConfigService.Save();
        }

        private static void OpenGuidance(string rclonePath, string openListPath)
        {
            var rcloneDir = Path.GetDirectoryName(rclonePath) ?? ConfigService.ConfigDirectory;
            if (!ShellPathService.TryOpenCommandPromptAt(rcloneDir, out var cmdError))
            {
                LogService.LogWarning(I18n.Format("CloudOnboarding_Log_OpenCommandPromptFailed", cmdError ?? string.Empty), nameof(CloudOnboardingService));
            }

            if (!string.IsNullOrWhiteSpace(openListPath))
            {
                var openListDir = Path.GetDirectoryName(openListPath) ?? ConfigService.ConfigDirectory;
                if (!ShellPathService.TryOpenPath(openListDir, out var openListError))
                {
                    LogService.LogWarning(I18n.Format("CloudOnboarding_Log_OpenOpenListFolderFailed", openListError ?? string.Empty), nameof(CloudOnboardingService));
                }
            }

            if (!ShellPathService.TryOpenPath(CloudGuideUrl, out var guideError))
            {
                LogService.LogWarning(I18n.Format("CloudOnboarding_Log_OpenGuideFailed", guideError ?? string.Empty), nameof(CloudOnboardingService));
            }
        }

        private static string SanitizePathSegment(string value)
        {
            var safe = string.IsNullOrWhiteSpace(value) ? "latest" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(safe) ? "latest" : safe;
        }
    }
}
