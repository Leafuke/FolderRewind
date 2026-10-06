using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class CloudOnboardingService
    {
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
                    "rclone",
                    progress,
                    ct).ConfigureAwait(false);

                string openListPath = string.Empty;
                if (provider.RequiresOpenList)
                {
                    openListPath = await EnsureToolAsync(
                        "openlist",
                        progress,
                        ct).ConfigureAwait(false);
                }

                progress?.Report(I18n.GetString("CloudOnboarding_Status_SaveSettings"));
                await ApplyToolPathsAsync(rclonePath, openListPath, ct).ConfigureAwait(false);

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

        private static Task<string> EnsureToolAsync(string tool, IProgress<string>? progress, CancellationToken ct)
        {
            var settings = ConfigService.CurrentConfig.GlobalSettings;
            var configuredPath = tool == "rclone" ? settings.RcloneExecutablePath : settings.OpenListRuntime.ExecutablePath;
            return OnboardingToolInstaller.EnsureAsync(tool, configuredPath,
                Path.Combine(ConfigService.ConfigDirectory, "tools"), RuntimeInformation.OSArchitecture,
                OnboardingToolInstaller.PathCandidates(tool + ".exe", Environment.GetEnvironmentVariable("PATH"))
                    .Concat(AppPathCandidates(tool + ".exe")),
                (asset, token) => GitHubReleaseService.DownloadVerifiedAssetAsync(asset.Url, asset.Sha256, token),
                ToolExecutableVerifier.VerifyAsync,
                message => { progress?.Report(message); LogService.LogInfo(message, nameof(CloudOnboardingService)); }, ct);
        }

        private static IEnumerable<string> AppPathCandidates(string executableName)
        {
            var paths = new List<string>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executableName);
                    if (key?.GetValue(null) is string path) paths.Add(path.Trim().Trim('"'));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
            return paths;
        }

        private static Task ApplyToolPathsAsync(string rclonePath, string openListPath, CancellationToken token)
            => UiDispatcherService.RunOnUiAsync(async () =>
            {
                var settings = ConfigService.CurrentConfig.GlobalSettings;
                var previousRclone = settings.RcloneExecutablePath;
                var openList = settings.OpenListRuntime;
                var previousOpenList = openList.ExecutablePath;
                await ConfigEditTransaction.ApplyAsync(() =>
                    {
                        settings.RcloneExecutablePath = rclonePath;
                        if (openListPath.Length != 0) openList.ExecutablePath = openListPath;
                    },
                    () => { settings.RcloneExecutablePath = previousRclone; openList.ExecutablePath = previousOpenList; },
                    () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
            });
    }
}
