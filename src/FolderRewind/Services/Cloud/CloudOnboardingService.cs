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
    internal sealed record PreparedCloudTools(string RcloneExecutablePath, string OpenListExecutablePath);

    public static class CloudOnboardingService
    {
        internal static async Task<PreparedCloudTools> PrepareRuntimeToolsAsync(bool includeOpenList, IProgress<string>? progress,
            CancellationToken token, string? preferredRclonePath = null)
        {
            var settings = ConfigService.CurrentConfig.GlobalSettings;
            var previousRclone = settings.RcloneExecutablePath;
            var previousOpenList = ConfigSnapshotMapper.Copy(settings.OpenListRuntime);
            var rclone = await EnsureToolAsync("rclone", preferredRclonePath?.Trim() ?? previousRclone,
                [previousRclone], false, progress, token).ConfigureAwait(false);
            var openList = "";
            if (includeOpenList)
            {
                if (File.Exists(previousOpenList.ConfigFilePath))
                {
                    // An existing instance can use a different version. Verify and reuse its
                    // executable without replacing it or applying the fresh initializer's limit.
                    await ToolExecutableVerifier.VerifyAsync(previousOpenList.ExecutablePath, "openlist", token).ConfigureAwait(false);
                    openList = previousOpenList.ExecutablePath;
                    ReportPreparationProgress(progress, I18n.GetString("Onboarding_ComponentReused") + " " + openList);
                }
                else openList = await EnsureToolAsync("openlist", previousOpenList.ExecutablePath, [], true, progress, token).ConfigureAwait(false);
            }
            ReportPreparationProgress(progress, I18n.GetString("CloudOnboarding_Status_SaveSettings"));
            await ApplyToolPathsAsync(rclone, openList, settings, previousRclone, previousOpenList, token).ConfigureAwait(false);
            return new(rclone, openList);
        }
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

                var prepared = await PrepareRuntimeToolsAsync(provider.RequiresOpenList, progress, ct).ConfigureAwait(false);
                var rclonePath = prepared.RcloneExecutablePath;
                var openListPath = prepared.OpenListExecutablePath;

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

        private static Task<string> EnsureToolAsync(string tool, string configuredPath, IEnumerable<string> configuredAlternates,
            bool requirePinnedOpenList, IProgress<string>? progress, CancellationToken ct)
        {
            return OnboardingToolInstaller.EnsureAsync(tool, configuredPath,
                Path.Combine(ConfigService.ConfigDirectory, "tools"), RuntimeInformation.OSArchitecture,
                OnboardingToolInstaller.PathCandidates(tool + ".exe", Environment.GetEnvironmentVariable("PATH"))
                    .Concat(AppPathCandidates(tool + ".exe")),
                (asset, token) => GitHubReleaseService.DownloadVerifiedAssetAsync(asset.Url, asset.Sha256, token),
                async (path, name, token) =>
                {
                    var version = await ToolExecutableVerifier.VerifyAsync(path, name, token).ConfigureAwait(false);
                    if (requirePinnedOpenList && !OpenListInitializer.Supports(version))
                    {
                        ReportPreparationProgress(progress, I18n.Format("CloudOnboarding_OpenListVersionSkipped", path, OpenListInitializer.SupportedVersion));
                        throw new InvalidOperationException("[openlist.initialization-version]");
                    }
                    return version;
                },
                message => ReportPreparationProgress(progress, message), ct, configuredAlternates: configuredAlternates);
        }

        private static void ReportPreparationProgress(IProgress<string>? progress, string message)
        { progress?.Report(message); LogService.LogInfo(message, nameof(CloudOnboardingService)); }

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

        private static Task ApplyToolPathsAsync(string rclonePath, string openListPath, GlobalSettings settings,
            string previousRclone, OpenListRuntimeSettings expectedOpenList, CancellationToken token)
            => UiDispatcherService.RunOnUiAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                void RequireExpected()
                {
                    if (!ReferenceEquals(settings, ConfigService.CurrentConfig.GlobalSettings)
                        || settings.RcloneExecutablePath != previousRclone
                        || openListPath.Length != 0 && !OpenListEnvironmentEdits.SameSnapshot(settings.OpenListRuntime, expectedOpenList))
                        throw new CloudSetupInputException("CloudOnboarding_SettingsChanged");
                }
                RequireExpected();
                var openList = settings.OpenListRuntime;
                var preparedOpenList = ConfigSnapshotMapper.Copy(openList);
                if (openListPath.Length != 0 && preparedOpenList.ExecutablePath != openListPath)
                {
                    if (OpenListRuntimeService.OwnsCurrent) throw new CloudSetupInputException("OpenList_StopBeforeChange");
                    preparedOpenList.ExecutablePath = openListPath;
                    OpenListEnvironmentEdits.ClearAuthorization(preparedOpenList);
                }
                var changeRclone = previousRclone != rclonePath;
                var changeOpenList = !OpenListEnvironmentEdits.SameSnapshot(openList, preparedOpenList);
                if (!changeRclone && !changeOpenList) return;
                var applied = false;
                await ConfigEditTransaction.ApplyAsync(() =>
                    {
                        RequireExpected(); applied = true;
                        if (changeRclone) settings.RcloneExecutablePath = rclonePath;
                        if (changeOpenList) settings.OpenListRuntime = preparedOpenList;
                    },
                    () =>
                    {
                        if (!applied || !ReferenceEquals(settings, ConfigService.CurrentConfig.GlobalSettings)) return;
                        if (changeRclone && settings.RcloneExecutablePath == rclonePath) settings.RcloneExecutablePath = previousRclone;
                        if (changeOpenList && ReferenceEquals(settings.OpenListRuntime, preparedOpenList)) settings.OpenListRuntime = openList;
                    },
                    () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
            });
    }
}
