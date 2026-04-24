using FolderRewind.Services.Plugins;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public sealed class MinecraftOnboardingResult
    {
        public bool Success { get; init; }

        public string Message { get; init; } = string.Empty;

        public bool MineBackupModReminderRequired { get; init; }
    }

    public static class MinecraftOnboardingService
    {
        private const string MineRewindPluginId = "com.folderrewind.minerewind";
        private const string MineRewindOwner = "Leafuke";
        private const string MineRewindRepo = "FolderRewind-Plugin-Minecraft";
        private const string KnotLinkInstallerUrl = "https://github.com/hxh230802/KnotLink/releases/download/v1.0.0/KnotLinkService-1.0.0.0-Installer.exe";
        private const string KnotLinkInstallerFileName = "KnotLinkService-1.0.0.0-Installer.exe";

        public static async Task<MinecraftOnboardingResult> InstallPresetAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            try
            {
                progress?.Report(I18n.GetString("MinecraftOnboarding_Status_EnablePluginSystem"));
                EnsurePluginSystemEnabled();

                progress?.Report(I18n.GetString("MinecraftOnboarding_Status_DownloadMineRewind"));
                var pluginResult = await PluginStoreService.DownloadAndInstallLatestZipAsync(
                    MineRewindOwner,
                    MineRewindRepo,
                    item => item.Name.Contains("MineRewind", StringComparison.OrdinalIgnoreCase)
                        || item.Name.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)
                        || item.Name.Contains("FolderRewind-Plugin", StringComparison.OrdinalIgnoreCase),
                    ct);

                if (!pluginResult.Success)
                {
                    LogService.LogWarning(I18n.Format("MinecraftOnboarding_MineRewindFailed_Log", pluginResult.Message), nameof(MinecraftOnboardingService));
                    NotificationService.ShowError(pluginResult.Message, I18n.GetString("MinecraftOnboarding_Title"));
                    return new MinecraftOnboardingResult
                    {
                        Success = false,
                        Message = pluginResult.Message
                    };
                }

                progress?.Report(I18n.GetString("MinecraftOnboarding_Status_EnableMineRewind"));
                PluginService.RefreshInstalledList();
                PluginService.SetPluginEnabled(MineRewindPluginId, true);
                PluginService.RefreshAndLoadEnabled();

                progress?.Report(I18n.GetString("MinecraftOnboarding_Status_EnableKnotLink"));
                EnableKnotLink();

                progress?.Report(I18n.GetString("MinecraftOnboarding_Status_DownloadKnotLink"));
                var installerPath = await DownloadKnotLinkInstallerAsync(ct);

                progress?.Report(I18n.GetString("MinecraftOnboarding_Status_RunKnotLinkInstaller"));
                LaunchInstaller(installerPath);

                var message = I18n.GetString("MinecraftOnboarding_Success");
                NotificationService.ShowSuccess(message, I18n.GetString("MinecraftOnboarding_Title"), 8000);
                return new MinecraftOnboardingResult
                {
                    Success = true,
                    Message = message,
                    MineBackupModReminderRequired = true
                };
            }
            catch (OperationCanceledException)
            {
                var message = I18n.GetString("Common_Canceled");
                LogService.LogWarning(I18n.GetString("MinecraftOnboarding_Canceled_Log"), nameof(MinecraftOnboardingService));
                NotificationService.ShowWarning(message, I18n.GetString("MinecraftOnboarding_Title"));
                return new MinecraftOnboardingResult
                {
                    Success = false,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                var message = I18n.Format("MinecraftOnboarding_Failed", ex.Message);
                LogService.LogError(message, nameof(MinecraftOnboardingService), ex);
                NotificationService.ShowError(message, I18n.GetString("MinecraftOnboarding_Title"));
                return new MinecraftOnboardingResult
                {
                    Success = false,
                    Message = message
                };
            }
        }

        private static void EnsurePluginSystemEnabled()
        {
            if (!PluginService.IsPluginSystemEnabled())
            {
                PluginService.SetPluginSystemEnabled(true);
            }
        }

        private static void EnableKnotLink()
        {
            var settings = ConfigService.CurrentConfig.GlobalSettings;
            settings.EnableKnotLink = true;
            ConfigService.Save();
            KnotLinkService.Initialize();
        }

        private static async Task<string> DownloadKnotLinkInstallerAsync(CancellationToken ct)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FolderRewind", "MinecraftOnboarding");
            Directory.CreateDirectory(tempDir);

            var installerPath = Path.Combine(tempDir, KnotLinkInstallerFileName);
            var bytes = await GitHubReleaseService.DownloadAssetAsync(KnotLinkInstallerUrl, ct);
            await File.WriteAllBytesAsync(installerPath, bytes, ct);
            return installerPath;
        }

        private static void LaunchInstaller(string installerPath)
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                throw new FileNotFoundException(I18n.GetString("MinecraftOnboarding_KnotLinkInstallerMissing"), installerPath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "open"
            });

            LogService.LogInfo(I18n.Format("MinecraftOnboarding_KnotLinkInstallerStarted_Log", installerPath), nameof(MinecraftOnboardingService));
        }
    }
}
