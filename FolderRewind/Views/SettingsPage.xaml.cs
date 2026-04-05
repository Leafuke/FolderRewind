using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page, INotifyPropertyChanged
    {
        private readonly SettingsPageViewModel _viewModel = new();

        public GlobalSettings Settings => _viewModel.Settings;

        public int CloseBehaviorSelectedIndex
        {
            get => _viewModel.CloseBehaviorSelectedIndex;
            set => _viewModel.CloseBehaviorSelectedIndex = value;
        }

        public string AppVersion => _viewModel.AppVersion;

        public ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins => _viewModel.InstalledPlugins;

        private bool _isInitializingLanguage;
        private bool _isInitializingFont;
        // Toggle text for localized On/Off labels
        public string ToggleOnText { get; } = I18n.GetString("Common_ToggleOn");
        public string ToggleOffText { get; } = I18n.GetString("Common_ToggleOff");

        public string KnotLinkStatusMessage => _viewModel.KnotLinkStatusMessage;

        public Brush KnotLinkStatusColor => _viewModel.KnotLinkStatusColor;

        public bool IsCoreValidationRunning => _viewModel.IsCoreValidationRunning;

        public bool IsCoreValidationIdle => _viewModel.IsCoreValidationIdle;

        public bool HasCoreValidationReport => _viewModel.HasCoreValidationReport;

        public string CoreValidationStatusText => _viewModel.CoreValidationStatusText;

        public string CoreValidationLastRunText => _viewModel.CoreValidationLastRunText;

        public string CoreValidationLastSummaryText => _viewModel.CoreValidationLastSummaryText;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public ObservableCollection<string> FontFamilies => _viewModel.FontFamilies;

        public ObservableCollection<object> HotkeyBindingsView => _viewModel.HotkeyBindingsView;

        public SettingsPage()
        {
            this.InitializeComponent();

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Initialize();
            FontFamilies.CollectionChanged -= OnFontFamiliesCollectionChanged;
            FontFamilies.CollectionChanged += OnFontFamiliesCollectionChanged;

            Unloaded -= OnSettingsPageUnloaded;
            Unloaded += OnSettingsPageUnloaded;

            _isInitializingLanguage = true;
            _isInitializingFont = true;
            try
            {
                if (LanguageCombo != null)
                {
                    LanguageCombo.SelectedIndex = _viewModel.GetLanguageSelectedIndex();
                }

                if (FontFamilyCombo != null)
                {
                    SyncFontFamilySelection();
                }

                if (FontSizeBox != null)
                {
                    FontSizeBox.Value = Settings.BaseFontSize;
                }

            }
            finally
            {
                _isInitializingLanguage = false;
                _isInitializingFont = false;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                Bindings.Update();
                return;
            }

            OnPropertyChanged(e.PropertyName);
        }

        private void OnFontFamiliesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (FontFamilyCombo == null || _isInitializingFont)
            {
                return;
            }

            _isInitializingFont = true;
            try
            {
                SyncFontFamilySelection();
            }
            finally
            {
                _isInitializingFont = false;
            }
        }

        private void SyncFontFamilySelection()
        {
            if (FontFamilyCombo == null)
            {
                return;
            }

            var current = FontFamilies.FirstOrDefault(f => string.Equals(f, Settings.FontFamily, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(current))
            {
                FontFamilyCombo.SelectedItem = current;
                return;
            }

            if (!string.IsNullOrWhiteSpace(Settings.FontFamily))
            {
                FontFamilyCombo.SelectedItem = Settings.FontFamily;
                return;
            }

            FontFamilyCombo.SelectedItem = FontFamilies.FirstOrDefault();
        }

        private void OnSettingsPageUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            FontFamilies.CollectionChanged -= OnFontFamiliesCollectionChanged;
            _viewModel.Dispose();
            Unloaded -= OnSettingsPageUnloaded;
        }

        private void OnCoreValidationStateChanged()
        {
            _viewModel.RefreshCoreValidationState();
            Bindings.Update();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _viewModel.OnNavigatedTo();
        }

        private void OnCloseBehaviorSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                _viewModel.HandleCloseBehaviorSelectionChanged(cb.SelectedIndex);
            }

            Bindings.Update();
        }

        private void OnRememberCloseBehaviorToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleRememberCloseBehaviorToggled(ts.IsOn);
            }

            Bindings.Update();
        }

        private void OnNotificationsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleNotificationsToggled(ts.IsOn);
            }
        }

        private void OnToastLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                _viewModel.HandleToastLevelChanged(cb.SelectedIndex);
            }
        }

        private void OnFileSizeWarningThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            _viewModel.HandleFileSizeWarningThresholdChanged(e.NewValue);
        }

        private void OnNoticesToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleNoticesToggled(ts.IsOn);
            }
        }

        private void OnUpdateReminderToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleUpdateReminderToggled(ts.IsOn);
            }
        }

        private void OnAppUpdateSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                _viewModel.HandleAppUpdateSourceChanged(cb.SelectedIndex);
            }

            Bindings.Update();
        }

        private void OnAppUpdateAutoFallbackToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleAppUpdateAutoFallbackToggled(ts.IsOn);
            }
        }

        private void OnAppUpdateCustomMirrorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _viewModel.HandleAppUpdateCustomMirrorChanged(tb.Text);
            }
        }

        private void HotkeyManager_DefinitionsChanged(object? sender, EventArgs e)
        {
            _viewModel.RefreshHotkeyBindingsView();
        }

        private void RefreshHotkeyBindingsView()
        {
            _viewModel.RefreshHotkeyBindingsView();
        }

        private HotkeyDefinition? FindHotkeyDefinition(string id)
        {
            return _viewModel.FindHotkeyDefinition(id);
        }

        private async void OnEditHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var hotkeyId = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(hotkeyId)) return;

            var def = FindHotkeyDefinition(hotkeyId);
            if (def == null) return;

            var captureBox = new TextBox
            {
                IsReadOnly = true,
                PlaceholderText = I18n.GetString("Hotkeys_CapturePlaceholder"),
                Text = HotkeyManager.GetEffectiveGestureString(hotkeyId),
                MinWidth = 260,
            };

            HotkeyGesture? captured = null;

            captureBox.KeyDown += (_, args) =>
            {
                try
                {
                    var key = args.Key;
                    if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu || key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows)
                    {
                        args.Handled = true;
                        return;
                    }

                    var mods = HotkeyModifiers.None;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Ctrl;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Alt;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Shift;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0
                        || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
                        mods |= HotkeyModifiers.Win;

                    captured = new HotkeyGesture(mods, key);
                    captureBox.Text = captured.Value.ToString();
                    args.Handled = true;
                }
                catch
                {
                }
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = def.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(def.Description))
                panel.Children.Add(new TextBlock { Text = def.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = I18n.GetString("Hotkeys_CaptureHint"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(captureBox);

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Hotkeys_EditDialogTitle"),
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Save"),
                SecondaryButtonText = I18n.GetString("Hotkeys_Disable"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                XamlRoot = this.XamlRoot,
                DefaultButton = ContentDialogButton.Primary,
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (captured == null)
                {
                    await ShowSimpleMessageAsync(I18n.GetString("Hotkeys_NoCapture"));
                    return;
                }

                // 冲突检测：同一 scope 不允许重复
                var candidate = captured.Value.ToString();
                var defs = HotkeyManager.GetDefinitionsSnapshot();
                foreach (var other in defs)
                {
                    if (string.Equals(other.Id, def.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (other.Scope != def.Scope) continue;

                    var otherGesture = HotkeyManager.GetEffectiveGestureString(other.Id);
                    if (string.Equals(otherGesture, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        await ShowSimpleMessageAsync(I18n.Format("Hotkeys_ConflictDialog", candidate, other.DisplayName));
                        return;
                    }
                }

                _viewModel.SetHotkeyOverride(hotkeyId, candidate);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _viewModel.SetHotkeyOverride(hotkeyId, string.Empty);
            }
        }

        private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var hotkeyId = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(hotkeyId)) return;

            _viewModel.ResetHotkeyOverride(hotkeyId);
        }

        private async Task ShowSimpleMessageAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Common_Tip"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = I18n.GetString("Common_Close"),
                XamlRoot = this.XamlRoot,
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnRunCoreValidationClick(object sender, RoutedEventArgs e)
        {
            if (CoreFeatureValidationService.IsRunning)
            {
                await ShowSimpleMessageAsync(I18n.GetString("CoreValidation_AlreadyRunning"));
                return;
            }

            var report = await CoreFeatureValidationService.RunValidationAsync(false);
            OnCoreValidationStateChanged();
            await ShowTextDialogAsync(I18n.GetString("CoreValidation_Report_Title"), report.ToDisplayText());
        }

        private async void OnViewCoreValidationReportClick(object sender, RoutedEventArgs e)
        {
            var report = CoreFeatureValidationService.LastReport;
            if (report == null)
            {
                await ShowSimpleMessageAsync(I18n.GetString("CoreValidation_Report_NoData"));
                return;
            }

            await ShowTextDialogAsync(I18n.GetString("CoreValidation_Report_Title"), report.ToDisplayText());
        }

        #region About

        private async Task ShowTextDialogAsync(string title, string content)
        {
            var tb = new TextBox
            {
                Text = content,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 320
            };

            var scroll = new ScrollViewer
            {
                Content = tb,
                MaxHeight = 560
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = scroll,
                CloseButtonText = I18n.GetString("Common_Close"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        // Removed obsolete about button handlers: copy-version, open-build-diag, privacy, license

        #endregion

        #region KnotLink 互联设置

        /// <summary>
        /// 更新 KnotLink 状态显示
        /// </summary>
        private void UpdateKnotLinkStatus()
        {
            _viewModel.UpdateKnotLinkStatus();
        }

        /// <summary>
        /// KnotLink 开关切换
        /// </summary>
        private void OnKnotLinkToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleKnotLinkToggled(ts.IsOn);
            }

            Bindings.Update();
        }

        /// <summary>
        /// KnotLink 设置更改
        /// </summary>
        private void OnKnotLinkSettingChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.HandleKnotLinkSettingChanged();
        }

        /// <summary>
        /// 重启 KnotLink 服务
        /// </summary>
        private async void OnKnotLinkRestartClick(object sender, RoutedEventArgs e)
        {
            var initialized = _viewModel.RestartKnotLinkService();

            // 显示提示
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLink_Title"),
                Content = initialized
                    ? I18n.GetString("SettingsPage_KnotLink_RestartSuccess")
                    : I18n.GetString("SettingsPage_KnotLink_RestartFailed"),
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 测试 KnotLink 连接
        /// </summary>
        private async void OnKnotLinkTestClick(object sender, RoutedEventArgs e)
        {
            if (!KnotLinkService.IsInitialized)
            {
                var errorDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkTest_Title"),
                    Content = I18n.GetString("SettingsPage_KnotLinkTest_NotInitialized"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(errorDialog);
                await errorDialog.ShowAsync();
                return;
            }

            // 发送测试事件
            KnotLinkService.BroadcastEvent("event=test;message=Hello from FolderRewind!");

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLinkTest_Title"),
                Content = I18n.GetString("SettingsPage_KnotLinkTest_Broadcasted"),
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 发送自定义信息（通过 OpenSocket 发送 SEND 指令）
        /// </summary>
        private async void OnKnotLinkSendCustomClick(object sender, RoutedEventArgs e)
        {
            if (!KnotLinkService.IsInitialized)
            {
                var errorDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                    Content = I18n.GetString("SettingsPage_KnotLinkTest_NotInitialized"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(errorDialog);
                await errorDialog.ShowAsync();
                return;
            }

            var inputBox = new TextBox
            {
                PlaceholderText = I18n.GetString("SettingsPage_KnotLinkSendCustom_Placeholder"),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinWidth = 360
            };

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                Content = inputBox,
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var message = inputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                var emptyDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                    Content = I18n.GetString("SettingsPage_KnotLinkSendCustom_Empty"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(emptyDialog);
                await emptyDialog.ShowAsync();
                return;
            }

            try
            {
                var response = await KnotLinkService.QueryAsync($"SEND {message}", 5000);

                var respDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_ResultTitle"),
                    Content = response,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(respDialog);
                await respDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var respDialog = new ContentDialog
                {
                    Title = I18n.GetString("Common_Failed"),
                    Content = ex.Message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(respDialog);
                await respDialog.ShowAsync();
            }
        }

        /// <summary>
        /// 重置 KnotLink 设置为默认值
        /// </summary>
        private void OnKnotLinkResetClick(object sender, RoutedEventArgs e)
        {
            _viewModel.HandleKnotLinkResetToDefault();
            Bindings.Update();
        }

        #endregion

        private void OnPluginsEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandlePluginsEnabledToggled(ts.IsOn);
                Bindings.Update();
            }
        }

        private async void OnOpenPluginStoreClick(object sender, RoutedEventArgs e)
        {
            if (!PluginService.IsPluginSystemEnabled()) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var dialog = new ContentDialog
            {
                Title = rl.GetString("Plugins_StoreDialogTitle"),
                CloseButtonText = rl.GetString("Common_Close"),
                XamlRoot = this.XamlRoot,
                DefaultButton = ContentDialogButton.Close,
                Content = new Frame()
            };

            if (dialog.Content is Frame frame)
            {
                frame.Navigate(typeof(PluginStorePage));
            }

            await dialog.ShowAsync();
        }

        private async void OnManualInstallPluginClick(object sender, RoutedEventArgs e)
        {
            if (!PluginService.IsPluginSystemEnabled()) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            picker.FileTypeFilter.Add(".zip");
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var res = await PluginService.InstallFromZipAsync(file.Path);

            var msg = new ContentDialog
            {
                Title = res.Success ? rl.GetString("Common_Done") : rl.GetString("Common_Failed"),
                Content = res.Message,
                CloseButtonText = rl.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(msg);
            await msg.ShowAsync();

            PluginService.RefreshInstalledList();
            Bindings.Update();
        }

        private void OnOpenPluginFolderClick(object sender, RoutedEventArgs e)
        {
            PluginService.OpenPluginFolder();
        }

        private async void OnPluginsExpanderExpanded(object? sender, object e)
        {
            await _viewModel.EnsurePluginsRefreshedAsync();
            Bindings.Update();
        }

        private void OnRefreshPluginsClick(object sender, RoutedEventArgs e)
        {
            PluginService.RefreshAndLoadEnabled();
            Bindings.Update();
        }

        private void OnPluginEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch ts) return;
            if (ts.DataContext is not InstalledPluginInfo plugin) return;

            _viewModel.HandlePluginEnabledToggled(plugin.Id, ts.IsOn);
        }

        private async void OnPluginUninstallClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var confirm = new ContentDialog
            {
                Title = rl.GetString("Plugins_UninstallTitle"),
                Content = string.Format(rl.GetString("Plugins_UninstallConfirm"), plugin.Name, plugin.Id),
                PrimaryButtonText = rl.GetString("Plugins_UninstallButton"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            // 应用主题
            ThemeService.ApplyThemeToDialog(confirm);

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var result = PluginService.Uninstall(plugin.Id);

            var msg = new ContentDialog
            {
                Title = result.Success ? rl.GetString("Common_Done") : rl.GetString("Common_Failed"),
                Content = result.Message,
                CloseButtonText = rl.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(msg);
            await msg.ShowAsync();

            PluginService.RefreshInstalledList();
            Bindings.Update();
        }

        private void OnPluginsAutoCheckUpdatesToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandlePluginsAutoCheckUpdatesToggled(ts.IsOn);
            }
        }

        private async void OnCheckPluginUpdatesClick(object sender, RoutedEventArgs e)
        {
            var rl = ResourceLoader.GetForViewIndependentUse();

            try
            {
                await PluginService.CheckAllPluginUpdatesAsync(respectAutoCheckSetting: false);
                Bindings.Update();

                var hasUpdates = InstalledPlugins.Any(p => p.HasUpdate && !string.IsNullOrWhiteSpace(p.UpdateDownloadUrl));
                var msg = new ContentDialog
                {
                    Title = rl.GetString("Common_Done"),
                    Content = hasUpdates
                        ? rl.GetString("PluginService_UpdatesAvailable")
                        : rl.GetString("PluginService_NoUpdatesAvailable"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();
            }
            catch (Exception ex)
            {
                var msg = new ContentDialog
                {
                    Title = rl.GetString("Common_Failed"),
                    Content = ex.Message,
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();
            }
        }

        private async void OnPluginUpdateClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            if (string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
            {
                var noUrl = new ContentDialog
                {
                    Title = rl.GetString("Common_Failed"),
                    Content = rl.GetString("PluginService_NoUpdateUrl"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(noUrl);
                await noUrl.ShowAsync();
                return;
            }

            var confirm = new ContentDialog
            {
                Title = rl.GetString("Plugins_UpdateTitle"),
                Content = string.Format(rl.GetString("Plugins_UpdateConfirm"), plugin.Name, plugin.Version, plugin.LatestVersion),
                PrimaryButtonText = rl.GetString("Plugins_UpdateButton"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            btn.IsEnabled = false;
            try
            {
                var result = await PluginService.UpdatePluginFromUrlAsync(plugin);

                var msg = new ContentDialog
                {
                    Title = result.Success ? rl.GetString("Common_Done") : rl.GetString("Common_Failed"),
                    Content = result.Message,
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();

                PluginService.RefreshInstalledList();
                Bindings.Update();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void OnPluginSettingsClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var defs = PluginService.GetSettingsDefinitions(plugin.Id);
            if (defs == null || defs.Count == 0)
            {
                var noSettings = new ContentDialog
                {
                    Title = rl.GetString("Plugins_SettingsTitle"),
                    Content = rl.GetString("Plugins_NoSettings"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(noSettings);
                await noSettings.ShowAsync();
                return;
            }

            var current = PluginService.GetPluginSettings(plugin.Id);

            var panel = new StackPanel { Spacing = 12 };
            var validation = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(validation);

            var getters = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in defs)
            {
                var key = def.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key)) continue;

                current.TryGetValue(key, out var curVal);
                var initial = curVal ?? def.DefaultValue ?? string.Empty;

                var header = new TextBlock { Text = def.DisplayName ?? key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                panel.Children.Add(header);

                if (!string.IsNullOrWhiteSpace(def.Description))
                {
                    panel.Children.Add(new TextBlock { Text = def.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                }

                switch (def.Type)
                {
                    case PluginSettingType.Boolean:
                        {
                            var toggle = new ToggleSwitch { IsOn = string.Equals(initial, "true", StringComparison.OrdinalIgnoreCase) };
                            panel.Children.Add(toggle);
                            getters[key] = () => toggle.IsOn ? "true" : "false";
                            break;
                        }
                    case PluginSettingType.Integer:
                        {
                            int.TryParse(initial, out var intVal);
                            var nb = new NumberBox { Value = intVal, Minimum = int.MinValue, Maximum = int.MaxValue, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                            panel.Children.Add(nb);
                            getters[key] = () => ((int)Math.Round(nb.Value)).ToString();
                            break;
                        }
                    case PluginSettingType.Path:
                    case PluginSettingType.String:
                    default:
                        {
                            var tb = new TextBox { Text = initial, PlaceholderText = def.IsRequired ? I18n.GetString("Common_Required") : string.Empty };
                            panel.Children.Add(tb);
                            getters[key] = () => tb.Text ?? string.Empty;
                            break;
                        }
                }

                // WinUI3 没有通用 Separator 控件（不同于 WPF），这里用留白分隔即可。
                panel.Children.Add(new TextBlock { Text = string.Empty, Height = 8 });
            }

            var scroll = new ScrollViewer { Content = panel, MaxHeight = 560 };

            var dialog = new ContentDialog
            {
                Title = I18n.Format("Plugins_SettingsDialogTitle", plugin.Name),
                Content = scroll,
                PrimaryButtonText = I18n.GetString("Common_Save"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.Primary) return;

                // 简单必填校验：缺失则阻止关闭
                foreach (var def in defs)
                {
                    if (!def.IsRequired) continue;
                    if (string.IsNullOrWhiteSpace(def.Key)) continue;
                    if (!getters.TryGetValue(def.Key, out var get)) continue;
                    var v = get();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        validation.Text = I18n.Format("Plugins_SettingsMissingRequired", def.DisplayName ?? def.Key);
                        args.Cancel = true;
                        return;
                    }
                }

                validation.Text = string.Empty;
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            foreach (var def in defs)
            {
                if (string.IsNullOrWhiteSpace(def.Key)) continue;
                if (!getters.TryGetValue(def.Key, out var get)) continue;
                PluginService.SetPluginSetting(plugin.Id, def.Key, get());
            }

            // 尽力让设置立即生效
            PluginService.TryReinitialize(plugin.Id);
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingLanguage) return;

            if (sender is ComboBox cb)
            {
                _viewModel.HandleLanguageChanged(cb.SelectedIndex);
            }
        }

        // 通用的设置变更处理 (用于不需要特殊逻辑的开关)
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            ConfigService.Save();
        }

        private void OnHistoryColorsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleHistoryColorsToggled(ts.IsOn);
            }
        }

        // 开机自启特殊处理（MSIX StartupTask API）
        private async void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                var desired = ts.IsOn;
                var result = await _viewModel.HandleRunOnStartupToggledAsync(desired);

                if (!result.Success && desired)
                {
                    ts.IsOn = false;

                    if (result.DisabledByUser)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = I18n.GetString("Startup_DisabledByUser_Title"),
                            Content = I18n.GetString("Startup_DisabledByUser_Content"),
                            CloseButtonText = I18n.GetString("Common_Ok"),
                            XamlRoot = this.XamlRoot
                        };
                        ThemeService.ApplyThemeToDialog(dialog);
                        await dialog.ShowAsync();
                    }
                }

                Bindings.Update();
            }
        }

        private void OnSilentStartupToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ts.IsOn = _viewModel.HandleSilentStartupToggled(ts.IsOn);
            }
            else if (!Settings.RunOnStartup)
            {
                _viewModel.HandleSilentStartupToggled(false);
            }

            Bindings.Update();
        }

        // 浏览 7z 路径
        private async void OnBrowse7zClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _viewModel.ApplySevenZipPath(file.Path);
            }
        }

        private async void OnBrowseDefaultBackupRootClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            MainWindowService.InitializePicker(picker);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                _viewModel.ApplyDefaultBackupRootPath(folder.Path);
            }
        }

        // 主题切换
        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                _viewModel.HandleThemeChanged(cb.SelectedIndex);
            }
        }

        private void OnLoggingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                _viewModel.HandleLoggingChanged(ts.IsOn);
            }
        }

        private void OnLogSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            _viewModel.HandleLogSizeChanged(e.NewValue);
        }

        private void OnRetentionChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            _viewModel.HandleRetentionChanged(e.NewValue);
        }

        private void OnOpenLogCenterClick(object sender, RoutedEventArgs e)
        {
            _ = NavigationService.NavigateTo("Logs");
        }

        private void OnStartupSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (ReferenceEquals(sender, StartupWidthBox))
            {
                _viewModel.HandleStartupSizeChanged(isWidth: true, newValue: e.NewValue);
            }
            else if (ReferenceEquals(sender, StartupHeightBox))
            {
                _viewModel.HandleStartupSizeChanged(isWidth: false, newValue: e.NewValue);
            }
        }

        private void OnApplyStartupSizeClick(object sender, RoutedEventArgs e)
        {
            _viewModel.HandleApplyStartupSize();
        }

        private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingFont) return;

            if (sender is ComboBox cb && cb.SelectedItem is string selected)
            {
                _viewModel.HandleFontFamilyChanged(selected);
            }
        }

        private void OnFontSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (_isInitializingFont) return;

            _viewModel.HandleFontSizeChanged(e.NewValue);
        }
        private void OnJoinGroupButtonClick(object sender, RoutedEventArgs e)
        => FlyoutBase.ShowAttachedFlyout(sender as FrameworkElement);

        // ========== 数据迁移 ==========

        private async void OnExportConfigClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            picker.SuggestedFileName = "FolderRewind_config";
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            bool ok = ConfigService.ExportConfig(file.Path);
            if (ok)
                ShowInfoBar(I18n.GetString("Settings_ExportConfigSuccess"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            else
                ShowInfoBar(I18n.GetString("Settings_ExportConfigFailed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }

        private async void OnImportConfigClick(object sender, RoutedEventArgs e)
        {
            var confirm = new ContentDialog
            {
                Title = I18n.GetString("Settings_ImportConfigConfirmTitle"),
                Content = new TextBlock { Text = I18n.GetString("Settings_ImportConfigConfirmContent"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            bool ok = ConfigService.ImportConfig(file.Path);
            if (ok)
            {
                ShowInfoBar(I18n.GetString("Settings_ImportConfigSuccess"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
                // 重新加载页面数据
                OnPropertyChanged(nameof(Settings));
            }
            else
            {
                ShowInfoBar(I18n.GetString("Settings_ImportConfigFailed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
        }

        private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var templates = TemplateService.GetTemplates()
                    .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (templates.Count == 0)
                {
                    ShowInfoBar(I18n.GetString("Settings_Template_Export_NoTemplates"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
                    return;
                }

                var templateCombo = new ComboBox
                {
                    Header = I18n.GetString("Settings_Template_SelectToExport"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                foreach (var template in templates)
                {
                    templateCombo.Items.Add(new ComboBoxItem { Content = template.Name, Tag = template });
                }
                templateCombo.SelectedIndex = 0;

                var chooseDialog = new ContentDialog
                {
                    Title = I18n.GetString("Settings_Template_SelectToExport"),
                    Content = templateCombo,
                    PrimaryButtonText = I18n.GetString("Common_Confirm"),
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(chooseDialog);

                if (await chooseDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                var selectedTemplate = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as ConfigTemplate;
                if (selectedTemplate == null)
                {
                    ShowInfoBar(I18n.GetString("Template_Export_TemplateNotFound"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                    return;
                }

                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                // 用专用扩展名把“模板分享包”和普通配置导出区分开，后续用户找文件也更直观。
                picker.FileTypeChoices.Add("FolderRewind Template", new List<string> { TemplateService.ShareFileExtension });
                picker.SuggestedFileName = $"FolderRewind_template_{SanitizeFileName(selectedTemplate.Name)}";
                MainWindowService.InitializePicker(picker);

                var file = await picker.PickSaveFileAsync();
                if (file == null) return;

                var ok = TemplateService.ExportTemplate(selectedTemplate.Id, file.Path, out var message);
                ShowInfoBar(message, ok ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[SettingsPage] Export template failed: {ex.Message}", nameof(SettingsPage), ex);
                ShowInfoBar(I18n.Format("Template_Export_Failed", ex.Message), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
        }

        private async void OnImportTemplateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                // WinUI FileOpenPicker 的过滤器对复杂后缀（如 .frtemplate.json）兼容性较差，统一使用 .json 后再做结构校验。
                picker.FileTypeFilter.Add(".json");
                MainWindowService.InitializePicker(picker);

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
                {
                    ShowInfoBar(I18n.GetString("Template_Import_FileNotFound"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                    LogService.LogWarning($"[SettingsPage] Import template file not found: {file.Path}", nameof(SettingsPage));
                    return;
                }

                var inspection = TemplateService.InspectImportTemplate(file.Path);
                if (!inspection.Success)
                {
                    ShowInfoBar(inspection.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                    return;
                }

                // 默认优先保留两份，先保护本地模板，再把“覆盖”作为用户明确选择。
                var strategy = TemplateService.TemplateImportConflictStrategy.KeepBoth;
                if (inspection.HasConflict)
                {
                    var conflictDialog = new ContentDialog
                    {
                        Title = I18n.GetString("Template_Import_ConflictTitle"),
                        Content = I18n.Format(
                            "Template_Import_ConflictContent",
                            inspection.Template?.Name ?? string.Empty,
                            inspection.ConflictTemplateName),
                        PrimaryButtonText = I18n.GetString("Template_Import_ConflictReplace"),
                        SecondaryButtonText = I18n.GetString("Template_Import_ConflictKeepBoth"),
                        CloseButtonText = I18n.GetString("Common_Cancel"),
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };
                    ThemeService.ApplyThemeToDialog(conflictDialog);

                    var conflictResult = await conflictDialog.ShowAsync();
                    if (conflictResult == ContentDialogResult.None)
                    {
                        return;
                    }

                    strategy = conflictResult == ContentDialogResult.Primary
                        ? TemplateService.TemplateImportConflictStrategy.ReplaceExisting
                        : TemplateService.TemplateImportConflictStrategy.KeepBoth;
                }

                var ok = TemplateService.ImportTemplate(file.Path, strategy, out var message);
                ShowInfoBar(message, ok ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[SettingsPage] Import template failed: {ex.Message}", nameof(SettingsPage), ex);
                ShowInfoBar(I18n.Format("Template_Import_Failed", ex.Message), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
        }

        private async void OnManageTemplatesClick(object sender, RoutedEventArgs e)
        {
            var dialog = new TemplateManagerDialog
            {
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnBrowseOfficialTemplatesClick(object sender, RoutedEventArgs e)
        {
            var item = await OfficialTemplateDialogService.PickTemplateAsync(
                this.XamlRoot,
                I18n.GetString("OfficialTemplates_BrowseTitle"));
            if (item == null)
            {
                return;
            }

            await ImportOfficialTemplateAsync(item);
        }

        private async void OnUseTemplateShareCodeClick(object sender, RoutedEventArgs e)
        {
            var item = await OfficialTemplateDialogService.PromptShareCodeAsync(this.XamlRoot);
            if (item == null)
            {
                return;
            }

            await ImportOfficialTemplateAsync(item);
        }

        private async void OnPrepareTemplateSubmissionClick(object sender, RoutedEventArgs e)
        {
            await TemplateSubmissionWorkflowService.RunAsync(this.XamlRoot);
        }

        private static void ApplySubmissionMetadata(ConfigTemplate template, string gameName, bool clearSteamAppId)
        {
            template.GameName = gameName?.Trim() ?? string.Empty;
            if (clearSteamAppId)
            {
                template.SteamAppId = null;
            }

            ConfigService.Save();
        }

        private async Task ExportTemplateSubmissionPackageAsync(ConfigTemplate selected, string gameName)
        {
            ApplySubmissionMetadata(selected, gameName, clearSteamAppId: true);

            var validation = TemplateService.ValidateTemplateForOfficialSharing(selected);
            if (!validation.Success)
            {
                ShowInfoBar(
                    validation.Errors.Count > 0 ? string.Join(Environment.NewLine, validation.Errors) : validation.Message,
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                return;
            }

            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("FolderRewind Template", new List<string> { TemplateService.ShareFileExtension });
            picker.SuggestedFileName = $"FolderRewind_submission_{SanitizeFileName(selected.Name)}";
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var ok = TemplateService.ExportTemplateSubmissionPackage(selected.Id, file.Path, out var summary, out var message);
            ShowInfoBar(message, ok ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            if (!ok)
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(summary);
            Clipboard.SetContent(package);

            var successDialog = new ContentDialog
            {
                Title = I18n.GetString("Template_Submission_SummaryTitle"),
                Content = new TextBox
                {
                    Text = summary,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    IsReadOnly = true,
                    MinHeight = 220
                },
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(successDialog);
            await successDialog.ShowAsync();
        }

        private async Task SubmitOfficialTemplateAsync(ConfigTemplate selected, string gameName)
        {
            ApplySubmissionMetadata(selected, gameName, clearSteamAppId: false);

            var authState = await GitHubOAuthService.GetAuthenticationStateAsync(true);
            if (!authState.IsAuthenticated)
            {
                var authResult = await GitHubOAuthService.SignInAsync(this.XamlRoot);
                ShowInfoBar(authResult.Message, authResult.Success ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                if (!authResult.Success)
                {
                    return;
                }
            }

            var statusText = new TextBlock
            {
                Text = I18n.GetString("GitHubSubmit_Progress_Starting"),
                TextWrapping = TextWrapping.Wrap
            };

            var progressDialogContent = new StackPanel { Spacing = 12 };
            progressDialogContent.Children.Add(new ProgressRing { IsActive = true, Width = 48, Height = 48 });
            progressDialogContent.Children.Add(statusText);

            var progressDialog = new ContentDialog
            {
                Title = I18n.GetString("TemplateSubmissionDialog_SubmitToGitHub"),
                Content = progressDialogContent,
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(progressDialog);

            using var cts = new System.Threading.CancellationTokenSource();
            GitHubTemplateSubmissionService.SubmissionResult? submitResult = null;
            var progress = new Progress<string>(message => statusText.Text = message);
            var submitTask = Task.Run(async () =>
            {
                submitResult = await GitHubTemplateSubmissionService.SubmitTemplateAsync(selected, progress, cts.Token);
                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    try
                    {
                        progressDialog.Hide();
                    }
                    catch
                    {
                    }
                });
            });

            var dialogResult = await progressDialog.ShowAsync();
            if (dialogResult == ContentDialogResult.None && submitResult == null)
            {
                cts.Cancel();
                ShowInfoBar(I18n.GetString("GitHubSubmit_Canceled"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
                return;
            }

            await submitTask;
            if (submitResult == null)
            {
                ShowInfoBar(I18n.GetString("GitHubSubmit_UnknownFailure"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                return;
            }

            ShowInfoBar(submitResult.Message, submitResult.Success ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            if (submitResult.Success && !string.IsNullOrWhiteSpace(submitResult.PullRequestUrl))
            {
                var resultDialog = new ContentDialog
                {
                    Title = I18n.GetString("GitHubSubmit_ResultTitle"),
                    Content = new TextBox
                    {
                        Text = I18n.Format("GitHubSubmit_ResultContent", submitResult.ShareCode, submitResult.PullRequestUrl),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        IsReadOnly = true,
                        MinHeight = 140
                    },
                    PrimaryButtonText = I18n.GetString("GitHubSubmit_OpenPullRequest"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(resultDialog);
                var result = await resultDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    _ = Launcher.LaunchUriAsync(new Uri(submitResult.PullRequestUrl));
                }
            }
        }

        private async Task ImportOfficialTemplateAsync(RemoteTemplateIndexItem item)
        {
            var importResult = await OfficialTemplateImportService.ImportTemplateAsync(this.XamlRoot, item);
            if (importResult.Canceled)
            {
                return;
            }

            ShowInfoBar(
                importResult.Message,
                importResult.Success ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }

        private async void OnExportHistoryClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            picker.SuggestedFileName = "FolderRewind_history";
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            bool ok = HistoryService.ExportHistory(file.Path);
            if (ok)
                ShowInfoBar(I18n.GetString("Settings_ExportHistorySuccess"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            else
                ShowInfoBar(I18n.GetString("Settings_ExportHistoryFailed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }

        private async void OnImportHistoryClick(object sender, RoutedEventArgs e)
        {
            var confirm = new ContentDialog
            {
                Title = I18n.GetString("Settings_ImportHistoryConfirmTitle"),
                Content = new TextBlock { Text = I18n.GetString("Settings_ImportHistoryConfirmContent"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = I18n.GetString("Settings_ImportHistoryMerge"),
                SecondaryButtonText = I18n.GetString("Settings_ImportHistoryReplace"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            var result = await confirm.ShowAsync();
            if (result == ContentDialogResult.None) return;

            bool merge = (result == ContentDialogResult.Primary);

            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var (ok, count) = HistoryService.ImportHistory(file.Path, merge);
            if (ok)
                ShowInfoBar(I18n.Format("Settings_ImportHistorySuccess", count.ToString()), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            else
                ShowInfoBar(I18n.GetString("Settings_ImportHistoryFailed"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "template";
            }

            var sanitized = name;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized;
        }

        private async void ShowInfoBar(string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Content = message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[SettingsPage] ShowInfoBar dialog failed: {ex.Message}", nameof(SettingsPage));
            }
        }

    }
}
