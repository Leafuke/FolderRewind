using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FolderRewind.ViewModels
{
    public sealed partial class SettingsPageViewModel : ViewModelBase, IDisposable
    {
        public void HandlePluginsEnabledToggled(bool isOn)
        {
            PluginService.SetPluginSystemEnabled(isOn);
            OnPropertyChanged(nameof(Settings));
        }

        public void HandlePluginsAutoCheckUpdatesToggled(bool isOn)
        {
            Settings.Plugins.AutoCheckUpdates = isOn;
            _isDirty = true;
        }

        public void HandlePluginEnabledToggled(string pluginId, bool isOn)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return;
            }

            PluginService.SetPluginEnabled(pluginId, isOn);
        }

        public async Task<MinecraftOnboardingResult> InstallMinecraftPresetAsync()
        {
            if (IsMinecraftPresetInstallRunning)
            {
                return new MinecraftOnboardingResult
                {
                    Success = false,
                    Message = MinecraftPresetStatusText
                };
            }

            IsMinecraftPresetInstallRunning = true;
            MinecraftPresetStatusText = I18n.GetString("MinecraftOnboarding_Status_Start");

            try
            {
                var progress = new Progress<string>(status =>
                {
                    MinecraftPresetStatusText = status;
                });

                var result = await MinecraftOnboardingService.InstallPresetAsync(progress);
                MinecraftPresetStatusText = result.Message;

                OnPropertyChanged(nameof(Settings));
                OnPropertyChanged(nameof(InstalledPlugins));
                UpdateKnotLinkStatus();
                return result;
            }
            finally
            {
                IsMinecraftPresetInstallRunning = false;
            }
        }

        public async Task<CloudOnboardingResult> StartCloudPresetAsync()
        {
            if (IsCloudPresetRunning)
            {
                return new CloudOnboardingResult
                {
                    Success = false,
                    Message = CloudPresetStatusText
                };
            }

            var provider = SelectedCloudPresetOption;
            if (provider == null)
            {
                var message = I18n.GetString("CloudOnboarding_NoProvider");
                CloudPresetStatusText = message;
                NotificationService.ShowWarning(message, I18n.GetString("CloudOnboarding_Title"));
                return new CloudOnboardingResult
                {
                    Success = false,
                    Message = message
                };
            }

            IsCloudPresetRunning = true;
            CloudPresetStatusText = I18n.GetString("CloudOnboarding_Status_Start");

            try
            {
                var progress = new Progress<string>(status =>
                {
                    CloudPresetStatusText = status;
                });

                var result = await CloudOnboardingService.InstallPresetAsync(provider, progress);
                CloudPresetStatusText = result.Message;

                OnPropertyChanged(nameof(Settings));
                return result;
            }
            finally
            {
                IsCloudPresetRunning = false;
            }
        }

        public void HandleKnotLinkToggled(bool isOn)
        {
            Settings.EnableKnotLink = isOn;
            _isDirty = true;

            if (isOn)
            {
                KnotLinkService.Initialize();
            }
            else
            {
                KnotLinkService.Shutdown();
            }

            UpdateKnotLinkStatus();
        }

        public void HandleKnotLinkAutoStartToggled(bool isOn)
        {
            Settings.AutoStartKnotLinkServer = isOn;
            _isDirty = true;
        }

        public bool RestartKnotLinkService()
        {
            ConfigService.Save();
            KnotLinkService.Restart();
            UpdateKnotLinkStatus();
            return KnotLinkService.IsInitialized;
        }

        public void RefreshKnotLinkServerInfo()
        {
            KnotLinkServerInstalled = KnotLinkServerManagerService.IsServerInstalled();
            KnotLinkServerRunning = KnotLinkServerManagerService.IsServerProcessRunning();

            if (KnotLinkServerInstalled)
            {
                var version = KnotLinkServerManagerService.GetServerVersion();
                KnotLinkServerVersionText = version ?? I18n.GetString("SettingsPage_KnotLinkServerNotInstalled");
            }
            else
            {
                KnotLinkServerVersionText = I18n.GetString("SettingsPage_KnotLinkServerNotInstalled");
                KnotLinkServerHasUpdate = false;
                _knotLinkServerUpdateInfo = null;
            }

            KnotLinkServerStatusBrush = KnotLinkServerRunning
                ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                : KnotLinkServerInstalled
                    ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                    : new SolidColorBrush(Microsoft.UI.Colors.Gray);

            OnPropertyChanged(nameof(KnotLinkServerCanStart));
            OnPropertyChanged(nameof(KnotLinkServerUpdateEnabled));
        }

        public async Task<KnotLinkUpdateInfo?> CheckKnotLinkServerUpdateAsync()
        {
            KnotLinkServerUpdateChecking = true;
            try
            {
                var info = await KnotLinkServerManagerService.CheckForServerUpdateAsync();
                _knotLinkServerUpdateInfo = info;
                KnotLinkServerHasUpdate = info?.HasUpdate ?? false;
                _knotLinkServerLatestVersion = info?.LatestVersion ?? string.Empty;
                OnPropertyChanged(nameof(KnotLinkServerUpdateEnabled));
                return info;
            }
            finally
            {
                KnotLinkServerUpdateChecking = false;
            }
        }

        public bool StartKnotLinkServer()
        {
            var result = KnotLinkServerManagerService.TryStartServer();
            _ = Task.Run(async () =>
            {
                try
                {
                    if (result)
                    {
                        var host = string.IsNullOrWhiteSpace(Settings.KnotLinkHost)
                            ? "127.0.0.1"
                            : Settings.KnotLinkHost;
                        await KnotLinkServerManagerService.WaitForServerReadyAsync(host).ConfigureAwait(false);
                        if (Settings.EnableKnotLink)
                        {
                            KnotLinkService.Restart();
                        }
                    }

                    RefreshKnotLinkServerInfo();
                }
                catch { }
            });
            return result;
        }

        public async Task DownloadAndRunKnotLinkInstallerAsync()
        {
            if (_knotLinkServerUpdateInfo == null)
                throw new InvalidOperationException(I18n.GetString("SettingsPage_KnotLinkServerNoInstaller"));

            await KnotLinkServerManagerService.DownloadAndLaunchLatestInstallerAsync(
                _knotLinkServerUpdateInfo);
            NotificationService.ShowInfo(
                I18n.GetString("SettingsPage_KnotLinkServer_InstallerLaunched"),
                I18n.GetString("SettingsPage_KnotLink_Title"));
        }


        private void RefreshCloudPresetOptions()
        {
            CloudPresetOptions.Clear();
            foreach (var option in CloudOnboardingService.GetProviderOptions())
            {
                CloudPresetOptions.Add(option);
            }

            SelectedCloudPresetOption = CloudPresetOptions.FirstOrDefault();
        }

    }
}
