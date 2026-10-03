using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
                NotificationService.ShowError(
                    FolderRewind.Services.Plugins.V3.PluginV3PackageService.FormatRuntimeDiagnostics(
                        transition.Diagnostics));
            else if (transition.RequiresRestart)
                NotificationService.ShowWarning(I18n.GetString("Plugins_RuntimeRequiresRestart"));
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
            => TaskObserver.Observe(SetKnotLinkEnabledAsync(isOn), nameof(SettingsPageViewModel));

        public async Task SetKnotLinkEnabledAsync(bool isOn, CancellationToken cancellationToken = default)
        {
            if (isOn) ValidateKnotLinkSettings();
            var previous = Settings.EnableKnotLink;
            await ConfigEditTransaction.ApplyAsync(() => Settings.EnableKnotLink = isOn,
                () => Settings.EnableKnotLink = previous, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), cancellationToken);
            if (isOn) await KnotLinkService.InitializeAsync(cancellationToken);
            else await KnotLinkService.ShutdownAsync();
            UpdateKnotLinkStatus();
            RefreshKnotLinkServerInfo();
        }

        private void ValidateKnotLinkSettings()
        {
            var error = KnotLinkSettingsPolicy.Validate(Settings.KnotLinkHost, Settings.KnotLinkAppId,
                Settings.KnotLinkOpenSocketId, Settings.KnotLinkSignalId);
            if (error is not null) throw new ArgumentException(I18n.GetString(error));
        }

        public void HandleKnotLinkAutoStartToggled(bool isOn)
        {
            Settings.AutoStartKnotLinkServer = isOn;
            _isDirty = true;
        }

        public async Task<bool> RestartKnotLinkServiceAsync(CancellationToken cancellationToken = default)
        {
            ValidateKnotLinkSettings();
            await TaskObserver.SaveConfigAsync();
            await KnotLinkService.RestartAsync(cancellationToken);
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

            KnotLinkServerStatus = KnotLinkServerRunning
                ? SemanticStatus.Success
                : KnotLinkServerInstalled
                    ? SemanticStatus.Error
                    : SemanticStatus.Neutral;

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

        public async Task<bool> StartKnotLinkServerAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateKnotLinkSettings();
            var result = KnotLinkServerManagerService.TryStartServer();
            if (result)
            {
                var host = string.IsNullOrWhiteSpace(Settings.KnotLinkHost) ? "127.0.0.1" : Settings.KnotLinkHost.Trim();
                result = await KnotLinkServerManagerService.WaitForServerReadyAsync(host, ct: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result && Settings.EnableKnotLink) await KnotLinkService.RestartAsync(cancellationToken);
            }
            RefreshKnotLinkServerInfo();
            UpdateKnotLinkStatus();
            return result && KnotLinkServerRunning;
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
