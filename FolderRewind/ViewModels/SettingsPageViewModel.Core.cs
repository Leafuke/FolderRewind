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
        private bool _initialized;
        private bool _pluginsRefreshed;
        private bool _pluginsRefreshing;
        private bool _fontFamiliesLoading;
        private bool _isMinecraftPresetInstallRunning;
        private bool _isCloudPresetRunning;
        private bool _isSponsorOperationRunning;
        private string _minecraftPresetStatusText = string.Empty;
        private string _cloudPresetStatusText = string.Empty;
        private CloudOnboardingProviderOption? _selectedCloudPresetOption;
        private static readonly object FontCacheLock = new();
        private static IReadOnlyList<string>? _cachedInstalledFontFamilies;

        private string _knotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Disabled");
        private Brush _knotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Gray);

        private string _knotLinkServerVersionText = I18n.GetString("SettingsPage_KnotLinkServerNotInstalled");
        private bool _knotLinkServerInstalled;
        private bool _knotLinkServerRunning;
        private bool _knotLinkServerHasUpdate;
        private bool _knotLinkServerUpdateChecking;
        private string _knotLinkServerLatestVersion = string.Empty;
        private KnotLinkUpdateInfo? _knotLinkServerUpdateInfo;
        private Brush _knotLinkServerStatusBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);

        private bool _isDirty;

        public GlobalSettings Settings => ConfigService.CurrentConfig.GlobalSettings;

        public int CloseBehaviorSelectedIndex
        {
            get => (int)Settings.CloseBehavior;
            set
            {
                var normalized = Math.Clamp(value, 0, 2);
                Settings.CloseBehavior = (CloseBehavior)normalized;

                if (Settings.CloseBehavior == CloseBehavior.Ask)
                {
                    Settings.RememberCloseBehavior = false;
                }

                // 标记为脏，离开页面时统一保存。
                _isDirty = true;
                OnPropertyChanged();
            }
        }

        public string AppVersion => GetAppVersionString();

        public ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins => PluginService.InstalledPlugins;

        public ObservableCollection<string> FontFamilies { get; } = new();

        public ObservableCollection<string> SponsorAccentPresets { get; } = new();

        public ObservableCollection<string> SponsorBackdropPresets { get; } = new();

        public ObservableCollection<string> SponsorTitleIconGlyphs { get; } = new();

        public ObservableCollection<string> SponsorBackgroundStretchModes { get; } = new();

        public ObservableCollection<string> CompletionSoundPresets { get; } = new();

        public ObservableCollection<object> HotkeyBindingsView { get; } = new();

        public ObservableCollection<CloudOnboardingProviderOption> CloudPresetOptions { get; } = new();

        public IAsyncRelayCommand InstallMinecraftPresetCommand { get; }

        public IAsyncRelayCommand StartCloudPresetCommand { get; }

        public IAsyncRelayCommand PurchaseSponsorCommand { get; }

        public IRelayCommand OpenSponsorWindowCommand { get; }

        public IAsyncRelayCommand RestoreSponsorCommand { get; }

        public IAsyncRelayCommand RefreshSponsorCommand { get; }

        public IRelayCommand ClearSponsorBackgroundCommand { get; }

        public IRelayCommand PreviewCompletionSoundCommand { get; }

        public IRelayCommand ClearCustomCompletionSoundCommand { get; }

        public bool IsMinecraftPresetInstallRunning
        {
            get => _isMinecraftPresetInstallRunning;
            private set
            {
                if (!SetProperty(ref _isMinecraftPresetInstallRunning, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsMinecraftPresetInstallIdle));
                InstallMinecraftPresetCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsMinecraftPresetInstallIdle => !IsMinecraftPresetInstallRunning;

        public string MinecraftPresetStatusText
        {
            get => _minecraftPresetStatusText;
            private set => SetProperty(ref _minecraftPresetStatusText, value ?? string.Empty);
        }

        public bool IsCloudPresetRunning
        {
            get => _isCloudPresetRunning;
            private set
            {
                if (!SetProperty(ref _isCloudPresetRunning, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsCloudPresetIdle));
                StartCloudPresetCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsCloudPresetIdle => !IsCloudPresetRunning;

        public string CloudPresetStatusText
        {
            get => _cloudPresetStatusText;
            private set => SetProperty(ref _cloudPresetStatusText, value ?? string.Empty);
        }

        public CloudOnboardingProviderOption? SelectedCloudPresetOption
        {
            get => _selectedCloudPresetOption;
            set
            {
                if (!SetProperty(ref _selectedCloudPresetOption, value))
                {
                    return;
                }

                StartCloudPresetCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsSponsorUnlocked => SponsorService.IsUnlocked;

        public bool IsSponsorLocked => !SponsorService.IsUnlocked;

        public bool IsSponsorOperationRunning
        {
            get => _isSponsorOperationRunning;
            private set
            {
                if (!SetProperty(ref _isSponsorOperationRunning, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsSponsorOperationIdle));
                PurchaseSponsorCommand.NotifyCanExecuteChanged();
                RestoreSponsorCommand.NotifyCanExecuteChanged();
                RefreshSponsorCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsSponsorOperationIdle => !IsSponsorOperationRunning;

        public string SponsorStatusText => SponsorService.StatusMessage;

        public double SponsorBackgroundImageOpacityPercent
        {
            get => Math.Clamp(Settings.SponsorBackgroundImageOpacity, 0, 1) * 100d;
            set => HandleSponsorBackgroundImageOpacityChanged(value);
        }

        public double SponsorBackgroundOverlayOpacityPercent
        {
            get => Math.Clamp(Settings.SponsorBackgroundOverlayOpacity, 0, 1) * 100d;
            set => HandleSponsorBackgroundOverlayOpacityChanged(value);
        }

        public string KnotLinkStatusMessage
        {
            get => _knotLinkStatusMessage;
            private set => SetProperty(ref _knotLinkStatusMessage, value ?? string.Empty);
        }

        public Brush KnotLinkStatusColor
        {
            get => _knotLinkStatusColor;
            private set => SetProperty(ref _knotLinkStatusColor, value);
        }

        public string KnotLinkServerVersionText
        {
            get => _knotLinkServerVersionText;
            private set => SetProperty(ref _knotLinkServerVersionText, value);
        }

        public bool KnotLinkServerInstalled
        {
            get => _knotLinkServerInstalled;

            private set => SetProperty(ref _knotLinkServerInstalled, value);
        }

        public bool KnotLinkServerRunning
        {
            get => _knotLinkServerRunning;
            private set => SetProperty(ref _knotLinkServerRunning, value);
        }

        public bool KnotLinkServerHasUpdate
        {
            get => _knotLinkServerHasUpdate;
            private set => SetProperty(ref _knotLinkServerHasUpdate, value);
        }

        public bool KnotLinkServerUpdateChecking
        {
            get => _knotLinkServerUpdateChecking;
            private set => SetProperty(ref _knotLinkServerUpdateChecking, value);
        }

        public bool KnotLinkServerCanStart => KnotLinkServerInstalled && !KnotLinkServerRunning;

        public bool KnotLinkServerUpdateEnabled => !KnotLinkServerUpdateChecking && KnotLinkServerHasUpdate;

        public Brush KnotLinkServerStatusBrush
        {
            get => _knotLinkServerStatusBrush;
            private set => SetProperty(ref _knotLinkServerStatusBrush, value);
        }

        public bool IsCoreValidationRunning => CoreFeatureValidationService.IsRunning;

        public bool IsCoreValidationIdle => !CoreFeatureValidationService.IsRunning;

        public bool HasCoreValidationReport => CoreFeatureValidationService.LastReport != null;

        public string CoreValidationStatusText => CoreFeatureValidationService.StatusText;

        public string CoreValidationLastRunText
        {
            get
            {
                if (Settings.LastCoreValidationUtc == DateTime.MinValue)
                {
                    return I18n.GetString("CoreValidation_LastRun_None");
                }

                return I18n.Format("CoreValidation_LastRun_Value", Settings.LastCoreValidationUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            }
        }

        public string CoreValidationLastSummaryText => string.IsNullOrWhiteSpace(Settings.LastCoreValidationSummary)
            ? I18n.GetString("CoreValidation_LastSummary_None")
            : Settings.LastCoreValidationSummary;

        public SettingsPageViewModel()
        {
            InstallMinecraftPresetCommand = new AsyncRelayCommand(
                async () => { await InstallMinecraftPresetAsync(); },
                () => IsMinecraftPresetInstallIdle);

            StartCloudPresetCommand = new AsyncRelayCommand(
                async () => { await StartCloudPresetAsync(); },
                () => IsCloudPresetIdle && SelectedCloudPresetOption != null);

            PurchaseSponsorCommand = new AsyncRelayCommand(
                async () => { await RunSponsorOperationAsync(SponsorService.PurchaseAsync); },
                () => IsSponsorOperationIdle);

            OpenSponsorWindowCommand = new RelayCommand(MainWindowService.OpenSponsorWindow);

            RestoreSponsorCommand = new AsyncRelayCommand(
                async () => { await RunSponsorOperationAsync(SponsorService.RestoreAsync); },
                () => IsSponsorOperationIdle);

            RefreshSponsorCommand = new AsyncRelayCommand(
                async () => { await RunSponsorOperationAsync(() => SponsorService.RefreshLicenseAsync(true)); },
                () => IsSponsorOperationIdle);

            ClearSponsorBackgroundCommand = new RelayCommand(ClearSponsorBackground, () => SponsorService.IsUnlocked);
            PreviewCompletionSoundCommand = new RelayCommand(PreviewCompletionSound);
            ClearCustomCompletionSoundCommand = new RelayCommand(ClearCustomCompletionSound, () => SponsorService.IsUnlocked);

            RefreshCloudPresetOptions();
            RefreshSponsorOptionLists();
        }

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            // 仅做一次的初始化：加载静态数据并挂事件。
            _initialized = true;

            EnsureFontFamiliesLoaded();
            RefreshHotkeyBindingsView();
            UpdateKnotLinkStatus();
            RefreshKnotLinkServerInfo();
            RefreshCoreValidationState();
            RefreshSponsorState();

            try
            {
                HotkeyManager.DefinitionsChanged -= HotkeyManager_DefinitionsChanged;
                HotkeyManager.DefinitionsChanged += HotkeyManager_DefinitionsChanged;
            }
            catch
            {
            }

            CoreFeatureValidationService.StateChanged -= CoreFeatureValidationService_StateChanged;
            CoreFeatureValidationService.StateChanged += CoreFeatureValidationService_StateChanged;

            SponsorService.StateChanged -= SponsorService_StateChanged;
            SponsorService.StateChanged += SponsorService_StateChanged;
            SponsorService.StatusChanged -= SponsorService_StateChanged;
            SponsorService.StatusChanged += SponsorService_StateChanged;

            await Task.CompletedTask;
        }

        public void OnNavigatedTo()
        {
            UpdateKnotLinkStatus();
            RefreshKnotLinkServerInfo();
        }

        public void SaveIfDirty()
        {
            if (_isDirty)
            {
                ConfigService.Save();
                _isDirty = false;
            }
        }

        public async Task EnsurePluginsRefreshedAsync()
        {
            if (_pluginsRefreshed || _pluginsRefreshing)
            {
                return;
            }

            _pluginsRefreshing = true;
            try
            {
                // 先让出当前帧，避免在展开动画开始前同步阻塞 UI。
                await Task.Yield();

                PluginService.RefreshRuntimeUi();
                _pluginsRefreshed = true;
                OnPropertyChanged(nameof(InstalledPlugins));
            }
            catch
            {
            }
            finally
            {
                _pluginsRefreshing = false;
            }
        }

        public void Dispose()
        {
            // 与 Initialize 成对解绑，避免设置页被缓存后事件重复触发。
            CoreFeatureValidationService.StateChanged -= CoreFeatureValidationService_StateChanged;
            SponsorService.StateChanged -= SponsorService_StateChanged;
            SponsorService.StatusChanged -= SponsorService_StateChanged;
            try
            {
                HotkeyManager.DefinitionsChanged -= HotkeyManager_DefinitionsChanged;
            }
            catch
            {
            }
        }

    }
}
