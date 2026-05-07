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
    public sealed class SettingsPageViewModel : ViewModelBase, IDisposable
    {
        private bool _initialized;
        private bool _pluginsRefreshed;
        private bool _pluginsRefreshing;
        private bool _fontFamiliesLoading;
        private bool _isMinecraftPresetInstallRunning;
        private bool _isSponsorOperationRunning;
        private string _minecraftPresetStatusText = string.Empty;
        private static readonly object FontCacheLock = new();
        private static IReadOnlyList<string>? _cachedInstalledFontFamilies;

        private string _knotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Disabled");
        private Brush _knotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Gray);

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

                // 设置页采用“即改即存”，避免离开页面时丢改动。
                ConfigService.Save();
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

        public IAsyncRelayCommand InstallMinecraftPresetCommand { get; }

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

            RefreshSponsorOptionLists();
        }

        public void Initialize()
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
        }

        public void OnNavigatedTo()
        {
            UpdateKnotLinkStatus();
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

                PluginService.RefreshAndLoadEnabled();
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

        public void HandleCloseBehaviorSelectionChanged(int selectedIndex)
        {
            CloseBehaviorSelectedIndex = selectedIndex;
        }

        public void HandleRememberCloseBehaviorToggled(bool isOn)
        {
            Settings.RememberCloseBehavior = isOn;

            if (Settings.RememberCloseBehavior && Settings.CloseBehavior == CloseBehavior.Ask)
            {
                Settings.CloseBehavior = CloseBehavior.MinimizeToTray;
                OnPropertyChanged(nameof(CloseBehaviorSelectedIndex));
            }

            ConfigService.Save();
        }

        public void HandleNotificationsToggled(bool isOn)
        {
            Settings.EnableNotifications = isOn;
            ConfigService.Save();

            if (!isOn)
            {
                NotificationService.ClearBadge();
                return;
            }

            NotificationService.RefreshBadgeVisualState();
        }

        public void HandleToastLevelChanged(int selectedIndex)
        {
            Settings.ToastNotificationLevel = Math.Clamp(selectedIndex, 0, 3);
            ConfigService.Save();
        }

        public void HandleFileSizeWarningThresholdChanged(double newValue)
        {
            if (double.IsNaN(newValue))
            {
                return;
            }

            Settings.FileSizeWarningThresholdKB = (int)Math.Clamp(newValue, 0, 10240);
            ConfigService.Save();
        }

        public void HandleAutoDownloadMissingCloudBackupsBeforeRestoreToggled(bool isOn)
        {
            Settings.AutoDownloadMissingCloudBackupsBeforeRestore = isOn;
            ConfigService.Save();
        }

        public void HandleNoticesToggled(bool isOn)
        {
            Settings.EnableNotices = isOn;
            ConfigService.Save();
        }

        public void HandleUpdateReminderToggled(bool isOn)
        {
            Settings.EnableUpdateReminder = isOn;
            ConfigService.Save();
        }

        public void HandleAppUpdateSourceChanged(int selectedIndex)
        {
            Settings.AppUpdatePreferredSource = Math.Clamp(selectedIndex, 0, 3);
            ConfigService.Save();
        }

        public void HandleAppUpdateAutoFallbackToggled(bool isOn)
        {
            Settings.AppUpdateAutoFallback = isOn;
            ConfigService.Save();
        }

        public void HandleAppUpdateCustomMirrorChanged(string? customUrl)
        {
            var normalized = customUrl?.Trim() ?? string.Empty;
            if (string.Equals(Settings.AppUpdateCustomMirrorUrl, normalized, StringComparison.Ordinal))
            {
                return;
            }

            Settings.AppUpdateCustomMirrorUrl = normalized;
            ConfigService.Save();
        }

        public int GetLanguageSelectedIndex()
        {
            return LanguageToIndex(Settings.Language);
        }

        public void HandleLanguageChanged(int selectedIndex)
        {
            Settings.Language = IndexToLanguage(selectedIndex);
            ConfigService.Save();
            MainWindowService.UpdateWindowTitle();
        }

        public async Task<StartupToggleResult> HandleRunOnStartupToggledAsync(bool desired)
        {
            var success = await StartupService.SetStartupAsync(desired);

            // 这里以系统真实返回结果为准，不能只信 UI 的期望值。
            Settings.RunOnStartup = success && desired;
            if (!Settings.RunOnStartup)
            {
                Settings.SilentStartup = false;
            }

            ConfigService.Save();

            StartupTaskState state = StartupTaskState.Disabled;
            if (!success && desired)
            {
                state = await StartupService.GetStartupStateAsync();
            }

            return new StartupToggleResult
            {
                DesiredEnabled = desired,
                Success = success,
                StartupState = state
            };
        }

        public bool HandleSilentStartupToggled(bool requested)
        {
            Settings.SilentStartup = Settings.RunOnStartup && requested;
            ConfigService.Save();
            return Settings.SilentStartup;
        }

        public void ApplySevenZipPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Settings.SevenZipPath = path;
            ConfigService.Save();
        }

        public void ApplyRclonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Settings.RcloneExecutablePath = path;
            ConfigService.Save();
        }

        public void ApplyDefaultCloudRemoteBasePath(string path)
        {
            var normalized = path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            Settings.DefaultCloudRemoteBasePath = normalized;
            ConfigService.Save();
        }

        public void ApplyDefaultBackupRootPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Settings.DefaultBackupRootPath = path;
            ConfigService.Save();
        }

        public void HandleThemeChanged(int selectedIndex)
        {
            Settings.ThemeIndex = Math.Clamp(selectedIndex, 0, 2);
            MainWindowService.ApplyCurrentTheme();
            ThemeService.NotifyThemeChanged();
            ConfigService.Save();
        }

        public void HandleSponsorAccentChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorAccentColorIndex = Math.Clamp(selectedIndex, 0, ThemeService.SponsorAccentPresetCount - 1);
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackdropChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorBackdropIndex = Math.Clamp(selectedIndex, 0, 1);
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorTitleTextChanged(string? titleText)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorTitleText = titleText?.Trim() ?? string.Empty;
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorTitleIconChanged(string? glyph)
        {
            if (!SponsorService.IsUnlocked || string.IsNullOrWhiteSpace(glyph))
            {
                return;
            }

            Settings.SponsorTitleIconGlyph = glyph;
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
        }

        public async Task ApplySponsorBackgroundImageAsync(string path)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            if (await SponsorPersonalizationService.ApplyBackgroundImageAsync(path))
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void ClearSponsorBackground()
        {
            if (SponsorPersonalizationService.ClearBackgroundImage())
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void HandleSponsorBackgroundEnabledToggled(bool isOn)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorBackgroundEnabled = isOn;
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackgroundStretchChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorBackgroundStretchIndex = Math.Clamp(selectedIndex, 0, 2);
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackgroundImageOpacityChanged(double newValue)
        {
            if (!SponsorService.IsUnlocked || double.IsNaN(newValue))
            {
                return;
            }

            Settings.SponsorBackgroundImageOpacity = Math.Clamp(newValue / 100d, 0, 1);
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
            OnPropertyChanged(nameof(SponsorBackgroundImageOpacityPercent));
        }

        public void HandleSponsorBackgroundOverlayOpacityChanged(double newValue)
        {
            if (!SponsorService.IsUnlocked || double.IsNaN(newValue))
            {
                return;
            }

            Settings.SponsorBackgroundOverlayOpacity = Math.Clamp(newValue / 100d, 0, 1);
            ConfigService.Save();
            MainWindowService.ApplySponsorVisuals();
            OnPropertyChanged(nameof(SponsorBackgroundOverlayOpacityPercent));
        }

        public void HandleCompletionSoundChanged(int selectedIndex)
        {
            Settings.CompletionSoundIndex = Math.Clamp(selectedIndex, 0, CompletionSoundService.PresetCount - 1);
            ConfigService.Save();
        }

        public void PreviewCompletionSound()
        {
            CompletionSoundService.PreviewConfiguredSound();
        }

        public async Task ApplyCustomCompletionSoundAsync(string path)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            if (await CompletionSoundService.ApplyCustomSoundAsync(path))
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void ClearCustomCompletionSound()
        {
            if (CompletionSoundService.ClearCustomSound())
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void HandleLoggingChanged(bool isOn)
        {
            Settings.EnableFileLogging = isOn;
            PushLogOptions();
            ConfigService.Save();
        }

        public void HandleLogSizeChanged(double newValue)
        {
            Settings.MaxLogFileSizeMb = (int)Math.Clamp(newValue, 1, 50);
            PushLogOptions();
            ConfigService.Save();
        }

        public void HandleRetentionChanged(double newValue)
        {
            Settings.LogRetentionDays = (int)Math.Clamp(newValue, 1, 60);
            PushLogOptions();
            ConfigService.Save();
        }

        public void HandleHistoryColorsToggled(bool isOn)
        {
            Settings.UseHistoryStatusColors = isOn;
            ConfigService.Save();
        }

        public void HandleStartupSizeChanged(bool isWidth, double newValue)
        {
            if (isWidth)
            {
                Settings.StartupWidth = ClampWidth(newValue);
            }
            else
            {
                Settings.StartupHeight = ClampHeight(newValue);
            }

            ConfigService.Save();
        }

        public void HandleApplyStartupSize()
        {
            Settings.StartupWidth = ClampWidth(Settings.StartupWidth);
            Settings.StartupHeight = ClampHeight(Settings.StartupHeight);

            ConfigService.Save();
            ApplyWindowSize(Settings.StartupWidth, Settings.StartupHeight);
        }

        public void HandleFontFamilyChanged(string selectedFontFamily)
        {
            if (string.IsNullOrWhiteSpace(selectedFontFamily))
            {
                return;
            }

            Settings.FontFamily = selectedFontFamily;
            TypographyService.ApplyTypography(Settings);
            ConfigService.Save();
        }

        public void HandleFontSizeChanged(double newSize)
        {
            Settings.BaseFontSize = Math.Clamp(newSize, 12, 20);
            TypographyService.ApplyTypography(Settings);
            ConfigService.Save();
        }

        public void HandlePluginsEnabledToggled(bool isOn)
        {
            PluginService.SetPluginSystemEnabled(isOn);
            OnPropertyChanged(nameof(Settings));
        }

        public void HandlePluginsAutoCheckUpdatesToggled(bool isOn)
        {
            Settings.Plugins.AutoCheckUpdates = isOn;
            ConfigService.Save();
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

        public void HandleKnotLinkToggled(bool isOn)
        {
            Settings.EnableKnotLink = isOn;
            ConfigService.Save();

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

        public void HandleKnotLinkSettingChanged()
        {
            ConfigService.Save();
        }

        public bool RestartKnotLinkService()
        {
            ConfigService.Save();
            KnotLinkService.Restart();
            UpdateKnotLinkStatus();
            return KnotLinkService.IsInitialized;
        }

        public void HandleKnotLinkResetToDefault()
        {
            Settings.KnotLinkHost = "127.0.0.1";
            Settings.KnotLinkAppId = "0x00000020";
            Settings.KnotLinkOpenSocketId = "0x00000010";
            Settings.KnotLinkSignalId = "0x00000020";
            ConfigService.Save();
            UpdateKnotLinkStatus();
        }

        public void RefreshCoreValidationState()
        {
            OnPropertyChanged(nameof(IsCoreValidationRunning));
            OnPropertyChanged(nameof(IsCoreValidationIdle));
            OnPropertyChanged(nameof(HasCoreValidationReport));
            OnPropertyChanged(nameof(CoreValidationStatusText));
            OnPropertyChanged(nameof(CoreValidationLastRunText));
            OnPropertyChanged(nameof(CoreValidationLastSummaryText));
        }

        public void RefreshSponsorState()
        {
            OnPropertyChanged(nameof(IsSponsorUnlocked));
            OnPropertyChanged(nameof(IsSponsorLocked));
            OnPropertyChanged(nameof(SponsorStatusText));
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(SponsorBackgroundImageOpacityPercent));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayOpacityPercent));
            ClearSponsorBackgroundCommand.NotifyCanExecuteChanged();
            ClearCustomCompletionSoundCommand.NotifyCanExecuteChanged();
        }

        public void RefreshHotkeyBindingsView()
        {
            HotkeyBindingsView.Clear();

            var defs = HotkeyManager.GetDefinitionsSnapshot();
            var overrides = Settings?.Hotkeys?.Bindings ?? new Dictionary<string, string>();

            // 构建纯展示模型，避免页面直接依赖 HotkeyDefinition 内部结构。
            foreach (var def in defs)
            {
                var effective = HotkeyManager.GetEffectiveGestureString(def.Id);
                var hasOverride = overrides.ContainsKey(def.Id);

                var scopeText = def.Scope == HotkeyScope.GlobalHotkey
                    ? I18n.GetString("Hotkeys_Scope_Global")
                    : I18n.GetString("Hotkeys_Scope_Shortcut");

                var ownerText = string.IsNullOrWhiteSpace(def.OwnerPluginId)
                    ? I18n.GetString("Hotkeys_Owner_Core")
                    : I18n.Format("Hotkeys_Owner_Plugin", def.OwnerPluginName ?? def.OwnerPluginId);

                HotkeyBindingsView.Add(new HotkeyBindingItem
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    Description = def.Description,
                    ScopeText = scopeText,
                    OwnerText = ownerText,
                    CurrentGesture = string.IsNullOrWhiteSpace(effective) ? I18n.GetString("Hotkeys_Unbound") : effective,
                    DefaultGesture = I18n.Format("Hotkeys_Default", string.IsNullOrWhiteSpace(def.DefaultGesture) ? I18n.GetString("Hotkeys_Unbound") : def.DefaultGesture),
                    IsOverridden = hasOverride,
                });
            }
        }

        public HotkeyDefinition? FindHotkeyDefinition(string id)
        {
            return HotkeyManager.GetDefinitionsSnapshot().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public void SetHotkeyOverride(string hotkeyId, string gesture)
        {
            HotkeyManager.SetGestureOverride(hotkeyId, gesture);
            RefreshHotkeyBindingsView();
        }

        public void ResetHotkeyOverride(string hotkeyId)
        {
            HotkeyManager.ResetGestureOverride(hotkeyId);
            RefreshHotkeyBindingsView();
        }

        public void UpdateKnotLinkStatus()
        {
            if (!Settings.EnableKnotLink)
            {
                KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Disabled");
                KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                return;
            }

            if (KnotLinkService.IsInitialized)
            {
                var responserOk = KnotLinkService.IsResponserRunning;
                var senderOk = KnotLinkService.IsSenderRunning;

                if (responserOk && senderOk)
                {
                    KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Connected");
                    KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                }
                else if (responserOk || senderOk)
                {
                    KnotLinkStatusMessage = I18n.Format(
                        "SettingsPage_KnotLinkStatus_Partial",
                        responserOk ? "✓" : "✗",
                        senderOk ? "✓" : "✗");
                    KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                }
                else
                {
                    KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_InitFailed");
                    KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                }
            }
            else
            {
                KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_NotInitialized");
                KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            }
        }

        private void EnsureFontFamiliesLoaded()
        {
            // 先同步放入基础字体，保证设置页首帧可交互。
            ApplyFontFamilies(GetFallbackFontFamilies(), persistWhenEmpty: false);

            if (_fontFamiliesLoading)
            {
                return;
            }

            _fontFamiliesLoading = true;
            _ = LoadFontFamiliesAsync();
        }

        private async Task LoadFontFamiliesAsync()
        {
            try
            {
                IReadOnlyList<string>? cached;
                lock (FontCacheLock)
                {
                    cached = _cachedInstalledFontFamilies;
                }

                if (cached == null)
                {
                    cached = await Task.Run(() =>
                    {
                        try
                        {
                            return FontService.GetInstalledFontFamilies();
                        }
                        catch
                        {
                            return GetFallbackFontFamilies();
                        }
                    }).ConfigureAwait(false);

                    lock (FontCacheLock)
                    {
                        _cachedInstalledFontFamilies ??= cached;
                        cached = _cachedInstalledFontFamilies;
                    }
                }

                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    ApplyFontFamilies(cached ?? GetFallbackFontFamilies(), persistWhenEmpty: true);
                });
            }
            finally
            {
                _fontFamiliesLoading = false;
            }
        }

        private IReadOnlyList<string> GetFallbackFontFamilies()
        {
            return new[] { "Segoe UI Variable", "Segoe UI", "Microsoft YaHei", "Microsoft YaHei UI" };
        }

        private void ApplyFontFamilies(IReadOnlyList<string> fonts, bool persistWhenEmpty)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in fonts)
            {
                if (!string.IsNullOrWhiteSpace(f))
                {
                    set.Add(f);
                }
            }

            foreach (var fallback in GetFallbackFontFamilies())
            {
                set.Add(fallback);
            }

            if (!string.IsNullOrWhiteSpace(Settings.FontFamily))
            {
                set.Add(Settings.FontFamily);
            }

            FontFamilies.Clear();
            foreach (var family in set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                FontFamilies.Add(family);
            }

            if (!persistWhenEmpty || !string.IsNullOrWhiteSpace(Settings.FontFamily))
            {
                return;
            }

            var preferred = PickPreferredFont(set);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                Settings.FontFamily = preferred;
                ConfigService.Save();
            }
        }

        private string PickPreferredFont(HashSet<string> availableFonts)
        {
            var preferChinese = string.Equals(Settings.Language, "zh-CN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Settings.Language, "zh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Settings.Language, "zh_CN", StringComparison.OrdinalIgnoreCase);

            if (preferChinese)
            {
                if (availableFonts.Contains("Microsoft YaHei UI"))
                {
                    return "Microsoft YaHei UI";
                }

                if (availableFonts.Contains("Microsoft YaHei"))
                {
                    return "Microsoft YaHei";
                }
            }

            if (availableFonts.Contains("Segoe UI Variable"))
            {
                return "Segoe UI Variable";
            }

            if (availableFonts.Contains("Segoe UI"))
            {
                return "Segoe UI";
            }

            return availableFonts.FirstOrDefault() ?? "Segoe UI";
        }

        private static string GetAppVersionString()
        {
            try
            {
                var v = Package.Current.Id.Version;
                return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                try
                {
                    var asm = typeof(SettingsPageViewModel).Assembly;
                    var v = asm.GetName().Version;
                    return v == null ? "Version (unknown)" : $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
                catch
                {
                    return "Version (unknown)";
                }
            }
        }

        private static int LanguageToIndex(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return 0;
            }

            var normalized = language.Trim().Replace('_', '-');
            if (string.Equals(normalized, "system", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(normalized, "en-US", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(normalized, "zh-CN", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(normalized, "zh", StringComparison.OrdinalIgnoreCase)) return 2;

            if (string.Equals(language, "en_US", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(language, "zh_CN", StringComparison.OrdinalIgnoreCase)) return 2;

            return 0;
        }

        private static string IndexToLanguage(int index)
        {
            return index switch
            {
                1 => "en-US",
                2 => "zh-CN",
                _ => "system",
            };
        }

        private static double ClampWidth(double value)
        {
            if (double.IsNaN(value) || value <= 0)
            {
                return 1200;
            }

            return Math.Clamp(value, 640, 3840);
        }

        private static double ClampHeight(double value)
        {
            if (double.IsNaN(value) || value <= 0)
            {
                return 800;
            }

            return Math.Clamp(value, 480, 2160);
        }

        private static void ApplyWindowSize(double width, double height)
        {
            var clampedWidth = ClampWidth(width);
            var clampedHeight = ClampHeight(height);
            MainWindowService.Resize(clampedWidth, clampedHeight);
        }

        private void RefreshSponsorOptionLists()
        {
            SponsorAccentPresets.Clear();
            for (var i = 0; i < ThemeService.SponsorAccentPresetCount; i++)
            {
                SponsorAccentPresets.Add(ThemeService.GetAccentPresetName(i));
            }

            SponsorBackdropPresets.Clear();
            SponsorBackdropPresets.Add(ThemeService.GetBackdropName(0));
            SponsorBackdropPresets.Add(ThemeService.GetBackdropName(1));

            SponsorTitleIconGlyphs.Clear();
            foreach (var glyph in IconCatalog.TitleIconGlyphs)
            {
                SponsorTitleIconGlyphs.Add(glyph);
            }

            SponsorBackgroundStretchModes.Clear();
            SponsorBackgroundStretchModes.Add(I18n.GetString("Sponsor_BackgroundStretch_UniformToFill"));
            SponsorBackgroundStretchModes.Add(I18n.GetString("Sponsor_BackgroundStretch_Uniform"));
            SponsorBackgroundStretchModes.Add(I18n.GetString("Sponsor_BackgroundStretch_Fill"));

            CompletionSoundPresets.Clear();
            for (var i = 0; i < CompletionSoundService.PresetCount; i++)
            {
                CompletionSoundPresets.Add(CompletionSoundService.GetPresetName(i));
            }
        }

        private async Task RunSponsorOperationAsync(Func<Task<SponsorOperationResult>> operation)
        {
            if (IsSponsorOperationRunning)
            {
                return;
            }

            IsSponsorOperationRunning = true;
            try
            {
                var result = await operation();
                if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
                {
                    LogService.LogWarning(result.Message, nameof(SettingsPageViewModel));
                }

                RefreshSponsorState();
            }
            finally
            {
                IsSponsorOperationRunning = false;
            }
        }

        private void PushLogOptions()
        {
            var options = new LogOptions
            {
                EnableFileLogging = Settings.EnableFileLogging,
                MaxEntries = 4000,
                MaxFileSizeKb = Math.Max(512, Settings.MaxLogFileSizeMb * 1024),
                RetentionDays = Math.Max(1, Settings.LogRetentionDays)
            };

            LogService.ApplyOptions(options);
        }

        private void HotkeyManager_DefinitionsChanged(object? sender, EventArgs e)
        {
            EnqueueOnUiThread(RefreshHotkeyBindingsView);
        }

        private void CoreFeatureValidationService_StateChanged()
        {
            EnqueueOnUiThread(RefreshCoreValidationState);
        }

        private void SponsorService_StateChanged()
        {
            EnqueueOnUiThread(RefreshSponsorState);
        }
    }

    public sealed class StartupToggleResult
    {
        public bool DesiredEnabled { get; init; }

        public bool Success { get; init; }

        public StartupTaskState StartupState { get; init; }

        public bool DisabledByUser => StartupState == StartupTaskState.DisabledByUser;
    }
}
