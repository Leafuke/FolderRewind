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
                await ApplyRclonePathAsync(rclonePath, ct).ConfigureAwait(false);

                var message = provider.RequiresOpenList
                    ? I18n.Format("CloudOnboarding_Success_WithOpenList", provider.DisplayName, rclonePath, openListPath, provider.SuggestedRemoteBasePath)
                    : I18n.Format("CloudOnboarding_Success_RcloneOnly", provider.DisplayName, rclonePath, provider.SuggestedRemoteBasePath);

                LogService.LogInfo(I18n.Format("CloudOnboarding_Log_Success", provider.Id, rclonePath, openListPath), nameof(CloudOnboardingService));
                message += Environment.NewLine + I18n.GetString("Onboarding_ToolsPrepared");
                NotificationService.ShowInfo(message, I18n.GetString("CloudOnboarding_Title"), 10000);

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
            if (toolDirectoryName == "rclone" && File.Exists(ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath))
            {
                var path = Path.GetFullPath(ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath);
                await ToolExecutableVerifier.VerifyAsync(path, toolDirectoryName, ct).ConfigureAwait(false);
                return path;
            }
            progress?.Report(downloadStatus);

            var release = await GitHubReleaseService.GetLatestReleaseAsync(owner, repo, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage))
            {
                throw new InvalidOperationException(release.ErrorMessage);
            }

            var tagName = string.IsNullOrWhiteSpace(release.TagName) ? "latest" : SanitizePathSegment(release.TagName);
            var installDir = Path.Combine(ConfigService.ConfigDirectory, "tools", toolDirectoryName, tagName);
            var asset = assetSelector(release.Assets)
                ?? throw new InvalidOperationException(I18n.Format("CloudOnboarding_AssetMissing", repo));
            if (!release.OfficialMetadata || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || asset.SizeBytes > ToolArchiveInstaller.MaximumArchiveBytes)
                throw new InvalidOperationException(I18n.GetString("Onboarding_ToolChecksumUnavailable"));

            var existingExecutable = FindExecutable(installDir, executableFileName);
            var marker = Path.Combine(installDir, ".folderrewind-tool-verification");
            if (!string.IsNullOrWhiteSpace(existingExecutable) && File.Exists(marker))
            {
                var evidence = File.ReadAllLines(marker);
                if (evidence.Length != 2 || evidence[0] != asset.Digest
                    || !StringComparer.OrdinalIgnoreCase.Equals(evidence[1], ToolExecutableVerifier.HashExecutable(existingExecutable)))
                    throw new InvalidDataException(I18n.GetString("Onboarding_ToolCacheChanged"));
                await ToolExecutableVerifier.VerifyAsync(existingExecutable, toolDirectoryName, ct).ConfigureAwait(false);
                LogService.LogInfo(I18n.Format("CloudOnboarding_Log_ReuseTool", toolDirectoryName, existingExecutable), nameof(CloudOnboardingService));
                return existingExecutable;
            }
            if (Directory.Exists(installDir)) throw new InvalidDataException(I18n.GetString("Onboarding_ToolCacheUnverified"));

            var downloadDir = Path.Combine(ConfigService.ConfigDirectory, "tools", "_downloads");
            Directory.CreateDirectory(downloadDir);
            var staging = installDir + ".staging-" + Guid.NewGuid().ToString("N");

            var zipPath = Path.Combine(downloadDir, $"{Guid.NewGuid():N}-{asset.Name}");
            try
            {
                var bytes = await GitHubReleaseService.DownloadAssetAsync(asset.DownloadUrl, ct, ToolArchiveInstaller.MaximumArchiveBytes).ConfigureAwait(false);
                await File.WriteAllBytesAsync(zipPath, bytes, ct).ConfigureAwait(false);

                var stagedExecutable = ToolArchiveInstaller.ExtractVerified(bytes, asset.Digest[7..], staging, executableFileName);
                await ToolExecutableVerifier.VerifyAsync(stagedExecutable, toolDirectoryName, ct).ConfigureAwait(false);
                File.WriteAllLines(Path.Combine(staging, ".folderrewind-tool-verification"), [asset.Digest, ToolExecutableVerifier.HashExecutable(stagedExecutable)]);
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(staging, stagedExecutable);
                Directory.Move(staging, installDir);
                var executable = Path.Combine(installDir, relative);
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
                // staging 为本次随机路径，始终位于本工具版本目录旁；不删除已有安装。
                if (Directory.Exists(staging) && Path.GetFullPath(staging).StartsWith(Path.GetFullPath(installDir) + ".staging-", StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(staging, recursive: true);
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

            var matches = Directory.EnumerateFiles(directory, executableFileName, SearchOption.AllDirectories).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : string.Empty;
        }

        private static Task ApplyRclonePathAsync(string rclonePath, CancellationToken token)
            => UiDispatcherService.RunOnUiAsync(async () =>
            {
                var settings = ConfigService.CurrentConfig.GlobalSettings;
                var previous = settings.RcloneExecutablePath;
                await ConfigEditTransaction.ApplyAsync(() => settings.RcloneExecutablePath = rclonePath,
                    () => settings.RcloneExecutablePath = previous, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
            });

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
