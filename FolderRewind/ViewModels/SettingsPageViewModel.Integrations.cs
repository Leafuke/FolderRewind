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
        public void HandlePluginsAutoCheckUpdatesToggled(bool isOn)
        {
            Settings.Plugins.AutoCheckUpdates = isOn;
            _isDirty = true;
        }

        public async Task HandlePluginEnabledToggledAsync(string pluginId, bool isOn)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return;
            }

            var id = new FolderRewind.Plugin.Abstractions.PluginId(pluginId);
            var transition = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.SetEnabledAsync(id, isOn);
            if (!transition.Success)
                NotificationService.ShowError(string.Join(", ", transition.Diagnostics.Select(value => value.Code)));
            PluginService.RefreshInstalledList();
            OnPropertyChanged(nameof(InstalledPlugins));
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

            // 初始化/关停涉及对服务端的 TCP 连接，远程主机不可达时可阻塞十余秒，
            // 必须移出 UI 线程执行；服务内部用信号量串行化，开关快速来回切换也安全。
            _ = Task.Run(async () =>
            {
                try
                {
                    if (isOn)
                    {
                        await KnotLinkService.InitializeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await KnotLinkService.ShutdownAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Log(I18n.Format("App_Log_KnotLinkInitException", ex.Message));
                }

                UpdateKnotLinkStatus();
            });
        }

        public void HandleKnotLinkAutoStartToggled(bool isOn)
        {
            Settings.AutoStartKnotLinkServer = isOn;
            _isDirty = true;
        }

        public async Task<bool> RestartKnotLinkServiceAsync()
        {
            ConfigService.Save();
            // 重启会重建对服务端的 TCP 连接，主机不可达时耗时较长，全程不占用 UI 线程。
            await KnotLinkService.RestartAsync().ConfigureAwait(false);
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
                            await KnotLinkService.RestartAsync().ConfigureAwait(false);
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
