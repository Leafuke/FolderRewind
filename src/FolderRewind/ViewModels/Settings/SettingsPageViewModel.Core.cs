using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
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
        private bool _active;
        private int _presentationGeneration;
        private bool _appearanceOptionsInitialized;
        private readonly HashSet<SettingsSection> _initializedSections = new();
        private readonly HashSet<SettingsSection> _initializingSections = new();
        private bool _pluginsRefreshed;
        private Task? _pluginsRefreshTask;
        private int _pluginsRefreshGeneration;
        private bool _isMinecraftPresetInstallRunning;
        private bool _isCloudPresetRunning;
        private bool _isSponsorOperationRunning;
        private string _minecraftPresetStatusText = string.Empty;
        private string _cloudPresetStatusText = string.Empty;
        private CloudOnboardingProviderOption? _selectedCloudPresetOption;

        private string _knotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Disabled");
        private SemanticStatus _knotLinkStatus = SemanticStatus.Neutral;

        private string _knotLinkServerVersionText = I18n.GetString("SettingsPage_KnotLinkServerNotInstalled");
        private bool _knotLinkServerInstalled;
        private bool _knotLinkServerRunning;
        private bool _knotLinkServerHasUpdate;
        private bool _knotLinkServerUpdateChecking;
        private string _knotLinkServerLatestVersion = string.Empty;
        private KnotLinkUpdateInfo? _knotLinkServerUpdateInfo;
        private SemanticStatus _knotLinkServerStatus = SemanticStatus.Neutral;

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

        public SemanticStatus KnotLinkStatus
        {
            get => _knotLinkStatus;
            private set => SetProperty(ref _knotLinkStatus, value);
        }

        public string KnotLinkStatusGlyph => "\uE774";

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

        public SemanticStatus KnotLinkServerStatus
        {
            get => _knotLinkServerStatus;
            private set => SetProperty(ref _knotLinkServerStatus, value);
        }

        public string KnotLinkServerStatusGlyph => "\uE7F8";

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

                return I18n.Format(
                    "CoreValidation_LastRun_Value",
                    UserDisplayFormatter.LongDateTime(Settings.LastCoreValidationUtc.ToLocalTime()));
            }
        }

        public string CoreValidationLastSummaryText => Settings.LastCoreValidationUtc == DateTime.MinValue
            ? I18n.GetString("CoreValidation_LastSummary_None")
            : Settings.LastCoreValidationPassed ? CoreFeatureValidationService.LastReport?.Summary ?? I18n.GetString("CoreValidation_Summary_LastPassed")
            : string.IsNullOrWhiteSpace(Settings.LastCoreValidationSummary) ? I18n.GetString("CoreValidation_Summary_Failed")
            : Settings.LastCoreValidationSummary;

        public SettingsPageViewModel()
        {
            InstallMinecraftPresetCommand = new AsyncRelayCommand(
                async () => { await InstallMinecraftPresetAsync(); },
                () => IsMinecraftPresetInstallIdle);

            StartCloudPresetCommand = new AsyncRelayCommand(
                () => { OpenCloudPreset(); return Task.CompletedTask; },
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

            ClearSponsorBackgroundCommand = new AsyncRelayCommand(ClearSponsorBackgroundAsync, () => SponsorService.IsUnlocked);
            PreviewCompletionSoundCommand = new RelayCommand(PreviewCompletionSound);
            ClearCustomCompletionSoundCommand = new AsyncRelayCommand(ClearCustomCompletionSoundAsync, () => SponsorService.IsUnlocked);

        }

        public void Activate()
        {
            if (_active) return;
            _active = true;
            _presentationGeneration++;
            ObserveBindableSettings();
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
            MainWindowService.SizeLimitsChanged -= RefreshWindowSizeBounds;
            MainWindowService.SizeLimitsChanged += RefreshWindowSizeBounds;

            SponsorService.StateChanged -= SponsorService_StateChanged;
            SponsorService.StateChanged += SponsorService_StateChanged;
            SponsorService.StatusChanged -= SponsorService_StateChanged;
            SponsorService.StatusChanged += SponsorService_StateChanged;

        }

        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            _presentationGeneration++;
            _initializedSections.Clear();
            _initializingSections.Clear();
            _pluginsRefreshed = false;
            _pluginsRefreshTask = null;
            StopObservingBindableSettings();
            CoreFeatureValidationService.StateChanged -= CoreFeatureValidationService_StateChanged;
            MainWindowService.SizeLimitsChanged -= RefreshWindowSizeBounds;
            SponsorService.StateChanged -= SponsorService_StateChanged;
            SponsorService.StatusChanged -= SponsorService_StateChanged;
            HotkeyManager.DefinitionsChanged -= HotkeyManager_DefinitionsChanged;
        }

        public void SaveIfDirty()
        {
            TaskObserver.Observe(SaveIfDirtyAsync(), nameof(SettingsPageViewModel));
        }

        private async Task SaveIfDirtyAsync()
        {
            // ForceExitRequested is set only after the final configuration save succeeds
            // and the writer is sealed. Window unload must not enqueue a second save.
            // Do not use IsShuttingDown: an attempted exit can still fail or be canceled.
            if (!_isDirty || App.ForceExitRequested) return;
            _isDirty = false;
            try { await TaskObserver.SaveConfigAsync(); }
            catch (Exception ex)
            {
                _isDirty = true;
                NotificationService.ShowError(ex.Message);
                throw;
            }
        }

        public Task EnsurePluginsRefreshedAsync()
        {
            if (!_active || _pluginsRefreshed) return Task.CompletedTask;
            if (_pluginsRefreshTask is not null && _pluginsRefreshGeneration == _presentationGeneration)
                return _pluginsRefreshTask;
            _pluginsRefreshGeneration = _presentationGeneration;
            return _pluginsRefreshTask = RefreshPluginsForPresentationAsync(_presentationGeneration);
        }

        private async Task RefreshPluginsForPresentationAsync(int generation)
        {
            try
            {
                // 先让出当前帧，避免在展开动画开始前同步阻塞 UI。
                await Task.Yield();
                if (!_active || generation != _presentationGeneration) return;

                PluginService.RefreshRuntimeUi();
                _pluginsRefreshed = true;
                OnPropertyChanged(nameof(InstalledPlugins));
            }
            catch (Exception ex)
            {
                LogService.LogError(ex.Message, nameof(SettingsPageViewModel), ex);
                throw;
            }
            finally
            {
                if (generation == _presentationGeneration) _pluginsRefreshTask = null;
            }
        }


        internal async Task InitializeSectionAsync(SettingsSection section)
        {
            if (!_active || _initializedSections.Contains(section) || !_initializingSections.Add(section)) return;
            var generation = _presentationGeneration;
            try
            {
                switch (section)
                {
                    case SettingsSection.Presets:
                        RefreshCloudPresetOptions();
                        break;
                    case SettingsSection.CoreBehavior:
                        await RefreshStartupStatusAsync();
                        if (!StartupStateKnown) return;
                        break;
                    case SettingsSection.Hotkeys:
                        RefreshHotkeyBindingsView();
                        break;
                    case SettingsSection.Appearance:
                    case SettingsSection.Sponsor:
                        if (!_appearanceOptionsInitialized)
                        {
                            RefreshSponsorOptionLists();
                            _appearanceOptionsInitialized = true;
                        }
                        RefreshSponsorState();
                        break;
                    case SettingsSection.CoreValidation:
                        RefreshCoreValidationState();
                        break;
                    case SettingsSection.Plugins:
                        await EnsurePluginsRefreshedAsync();
                        break;
                    case SettingsSection.KnotLink:
                        UpdateKnotLinkStatus();
                        var server = await Task.Run(() => (
                            Installed: KnotLinkServerManagerService.IsServerInstalled(),
                            Running: KnotLinkServerManagerService.IsServerProcessRunning(),
                            Version: KnotLinkServerManagerService.GetServerVersion()));
                        if (!_active || generation != _presentationGeneration) return;
                        ApplyKnotLinkServerInfo(server.Installed, server.Running, server.Version);
                        break;
                }
                if (_active && generation == _presentationGeneration) _initializedSections.Add(section);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (_active && generation == _presentationGeneration)
                    NotificationService.ShowError(error.Message);
                throw;
            }
            finally
            {
                if (generation == _presentationGeneration) _initializingSections.Remove(section);
            }
        }

        public void Dispose() => Deactivate();

    }
}
