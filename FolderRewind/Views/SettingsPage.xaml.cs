using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.Resources;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.Graphics;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page, INotifyPropertyChanged
    {
        public GlobalSettings Settings => ConfigService.CurrentConfig.GlobalSettings;

        public int CloseBehaviorSelectedIndex
        {
            get => (int)Settings.CloseBehavior;
            set
            {
                if (value < 0 || value > 2) value = 0;
                Settings.CloseBehavior = (CloseBehavior)value;

                // 选择“每次询问”时不再记住
                if (Settings.CloseBehavior == CloseBehavior.Ask)
                {
                    Settings.RememberCloseBehavior = false;
                }

                ConfigService.Save();
                OnPropertyChanged();
            }
        }

        public string AppVersion => GetAppVersionString();

        public ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins => PluginService.InstalledPlugins;

        private bool _isInitializingLanguage;
        private bool _isInitializingFont;

        // KnotLink 状态相关属性
        private string _knotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Disabled");
        private Brush _knotLinkStatusColor;

        public string KnotLinkStatusMessage
        {
            get => _knotLinkStatusMessage;
            set { _knotLinkStatusMessage = value; OnPropertyChanged(); }
        }

        public Brush KnotLinkStatusColor
        {
            get => _knotLinkStatusColor ??= new SolidColorBrush(Microsoft.UI.Colors.Gray);
            set { _knotLinkStatusColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public ObservableCollection<string> FontFamilies { get; } = new()
        {
        };

        public ObservableCollection<object> HotkeyBindingsView { get; } = new();

        public SettingsPage()
        {
            this.InitializeComponent();

            // Settings 页面打开时刷新一次插件列表，保证 UI 显示最新安装情况。
            try
            {
                PluginService.RefreshAndLoadEnabled();
            }
            catch
            {
            }

            _isInitializingLanguage = true;
            _isInitializingFont = true;
            try
            {
                LoadFontFamilies();

                if (LanguageCombo != null)
                {
                    LanguageCombo.SelectedIndex = LanguageToIndex(Settings.Language);
                }

                if (FontFamilyCombo != null)
                {
                    var current = FontFamilies.FirstOrDefault(f => string.Equals(f, Settings.FontFamily, StringComparison.OrdinalIgnoreCase));
                    FontFamilyCombo.SelectedItem = current ?? Settings.FontFamily ?? FontFamilies.FirstOrDefault();
                }

                if (FontSizeBox != null)
                {
                    FontSizeBox.Value = Settings.BaseFontSize;
                }

                // 初始化 KnotLink 状态显示
                UpdateKnotLinkStatus();

                // 快捷键/热键列表
                try
                {
                    HotkeyManager.DefinitionsChanged -= HotkeyManager_DefinitionsChanged;
                    HotkeyManager.DefinitionsChanged += HotkeyManager_DefinitionsChanged;
                }
                catch
                {
                }

                RefreshHotkeyBindingsView();
            }
            finally
            {
                _isInitializingLanguage = false;
                _isInitializingFont = false;
            }
        }

        private void OnCloseBehaviorSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // x:Bind TwoWay 已在 setter 保存，这里只做兜底刷新
            Bindings.Update();
        }

        private void OnRememberCloseBehaviorToggled(object sender, RoutedEventArgs e)
        {
            // 如果用户打开“记住”，但当前是 Ask，则默认切到“最小化到托盘”更符合预期
            if (Settings.RememberCloseBehavior && Settings.CloseBehavior == CloseBehavior.Ask)
            {
                Settings.CloseBehavior = CloseBehavior.MinimizeToTray;
                OnPropertyChanged(nameof(CloseBehaviorSelectedIndex));
            }

            ConfigService.Save();
            Bindings.Update();
        }

        private void OnNotificationsToggled(object sender, RoutedEventArgs e)
        {
            ConfigService.Save();

            // 如果关闭通知，清除 Badge
            if (!Settings.EnableNotifications)
            {
                NotificationService.ClearBadge();
            }
        }

        private void HotkeyManager_DefinitionsChanged(object? sender, EventArgs e)
        {
            _ = DispatcherQueue.TryEnqueue(RefreshHotkeyBindingsView);
        }

        private void RefreshHotkeyBindingsView()
        {
            HotkeyBindingsView.Clear();

            var defs = HotkeyManager.GetDefinitionsSnapshot();
            var overrides = Settings?.Hotkeys?.Bindings ?? new Dictionary<string, string>();

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

        private HotkeyDefinition? FindHotkeyDefinition(string id)
        {
            return HotkeyManager.GetDefinitionsSnapshot().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
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

                HotkeyManager.SetGestureOverride(hotkeyId, candidate);
                RefreshHotkeyBindingsView();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                HotkeyManager.SetGestureOverride(hotkeyId, string.Empty);
                RefreshHotkeyBindingsView();
            }
        }

        private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var hotkeyId = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(hotkeyId)) return;

            HotkeyManager.ResetGestureOverride(hotkeyId);
            RefreshHotkeyBindingsView();
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

        private void LoadFontFamilies()
        {
            FontFamilies.Clear();

            IReadOnlyList<string> fonts;
            try
            {
                fonts = FontService.GetInstalledFontFamilies();
            }
            catch
            {
                fonts = new[] { "Segoe UI Variable", "Segoe UI", "Microsoft YaHei", "Microsoft YaHei UI" };
            }

            foreach (var f in fonts)
            {
                if (!string.IsNullOrWhiteSpace(f)) FontFamilies.Add(f);
            }

            // 若当前配置字体不存在于系统列表，仍然保留它作为可选项
            if (!string.IsNullOrWhiteSpace(Settings.FontFamily)
                && !FontFamilies.Any(f => string.Equals(f, Settings.FontFamily, StringComparison.OrdinalIgnoreCase)))
            {
                FontFamilies.Insert(0, Settings.FontFamily);
            }

            // 若还未设置字体，则按推荐默认值设置一次
            if (string.IsNullOrWhiteSpace(Settings.FontFamily))
            {
                try
                {
                    Settings.FontFamily = FontService.GetRecommendedDefaultFontFamily();
                    ConfigService.Save();
                }
                catch
                {
                }
            }
        }

        #region About

        private static string GetAppVersionString()
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                // Unpackaged / test environment fallback
                try
                {
                    var asm = typeof(SettingsPage).Assembly;
                    var v = asm.GetName().Version;
                    return v == null ? "Version (unknown)" : $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
                catch
                {
                    return "Version (unknown)";
                }
            }
        }

        private static async Task<string> TryReadPackagedTextAsync(string relativePath)
        {
            try
            {
                var uri = new Uri($"ms-appx:///{relativePath.Replace('\\', '/')}");
                var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return string.Empty;
            }
        }

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

        /// <summary>
        /// KnotLink 开关切换
        /// </summary>
        private void OnKnotLinkToggled(object sender, RoutedEventArgs e)
        {
            ConfigService.Save();

            if (Settings.EnableKnotLink)
            {
                // 启用时自动初始化
                KnotLinkService.Initialize();
            }
            else
            {
                // 禁用时关闭服务
                KnotLinkService.Shutdown();
            }

            UpdateKnotLinkStatus();
        }

        /// <summary>
        /// KnotLink 设置更改
        /// </summary>
        private void OnKnotLinkSettingChanged(object sender, TextChangedEventArgs e)
        {
            ConfigService.Save();
        }

        /// <summary>
        /// 重启 KnotLink 服务
        /// </summary>
        private async void OnKnotLinkRestartClick(object sender, RoutedEventArgs e)
        {
            ConfigService.Save();
            KnotLinkService.Restart();
            UpdateKnotLinkStatus();

            // 显示提示
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLink_Title"),
                Content = KnotLinkService.IsInitialized
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
        /// 重置 KnotLink 设置为默认值
        /// </summary>
        private void OnKnotLinkResetClick(object sender, RoutedEventArgs e)
        {
            Settings.KnotLinkHost = "127.0.0.1";
            Settings.KnotLinkAppId = "0x00000020";
            Settings.KnotLinkOpenSocketId = "0x00000010";
            Settings.KnotLinkSignalId = "0x00000020";
            ConfigService.Save();
            Bindings.Update();
        }

        #endregion

        private void OnPluginsEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                PluginService.SetPluginSystemEnabled(ts.IsOn);
                // 配置保存由 PluginService 完成，这里同步 UI
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

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

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

        private void OnRefreshPluginsClick(object sender, RoutedEventArgs e)
        {
            PluginService.RefreshAndLoadEnabled();
            Bindings.Update();
        }

        private void OnPluginEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch ts) return;
            if (ts.DataContext is not InstalledPluginInfo plugin) return;

            PluginService.SetPluginEnabled(plugin.Id, ts.IsOn);
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
            ConfigService.Save();
        }

        private async void OnCheckPluginUpdatesClick(object sender, RoutedEventArgs e)
        {
            var rl = ResourceLoader.GetForViewIndependentUse();

            try
            {
                await PluginService.CheckAllPluginUpdatesAsync();
                Bindings.Update();

                var hasUpdates = InstalledPlugins.Any(p => p.HasUpdate);
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

        private static int LanguageToIndex(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return 0;

            var normalized = language.Trim().Replace('_', '-');
            if (string.Equals(normalized, "system", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(normalized, "en-US", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(normalized, "zh-CN", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(normalized, "zh", StringComparison.OrdinalIgnoreCase)) return 2;

            // legacy values
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

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingLanguage) return;

            if (sender is ComboBox cb)
            {
                Settings.Language = IndexToLanguage(cb.SelectedIndex);
                ConfigService.Save();
                App.UpdateWindowTitle();
            }
        }

        // 通用的设置变更处理 (用于不需要特殊逻辑的开关)
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            ConfigService.Save();
        }

        private void OnHistoryColorsToggled(object sender, RoutedEventArgs e)
        {
            // x:Bind TwoWay 已写回 Settings.UseHistoryStatusColors，这里只负责保存
            ConfigService.Save();
        }

        // 开机自启特殊处理（MSIX StartupTask API）
        private async void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                var desired = ts.IsOn;
                var success = await StartupService.SetStartupAsync(desired);

                Settings.RunOnStartup = success && desired;

                if (!success && desired)
                {
                    ts.IsOn = false;
                    
                    var state = await StartupService.GetStartupStateAsync();
                    if (state == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
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

                ConfigService.Save();
            }
        }

        // 浏览 7z 路径
        private async void OnBrowse7zClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                Settings.SevenZipPath = file.Path;
                ConfigService.Save();
            }
        }

        // 主题切换
        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                // 直接使用控件的 SelectedIndex，保证读取到最新选择
                var newIndex = cb.SelectedIndex;
                Settings.ThemeIndex = newIndex;
            }

            // 立即生效（主窗口 + 通知所有子窗口）
            ThemeService.ApplyThemeToWindow(App._window);
            ThemeService.NotifyThemeChanged();

            ConfigService.Save();
        }

        private void OnLoggingChanged(object sender, RoutedEventArgs e)
        {
            PushLogOptions();
            ConfigService.Save();
        }

        private void OnLogSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender is NumberBox nb)
            {
                Settings.MaxLogFileSizeMb = (int)Math.Clamp(nb.Value, 1, 50);
            }

            PushLogOptions();
            ConfigService.Save();
        }

        private void OnRetentionChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender is NumberBox nb)
            {
                Settings.LogRetentionDays = (int)Math.Clamp(nb.Value, 1, 60);
            }

            PushLogOptions();
            ConfigService.Save();
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

        private void OnOpenLogCenterClick(object sender, RoutedEventArgs e)
        {
            App.Shell?.NavigateTo("Logs");
        }

        private void OnStartupSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender == StartupWidthBox)
            {
                Settings.StartupWidth = ClampWidth(e.NewValue);
            }
            else if (sender == StartupHeightBox)
            {
                Settings.StartupHeight = ClampHeight(e.NewValue);
            }

            ConfigService.Save();
        }

        private void OnApplyStartupSizeClick(object sender, RoutedEventArgs e)
        {
            Settings.StartupWidth = ClampWidth(Settings.StartupWidth);
            Settings.StartupHeight = ClampHeight(Settings.StartupHeight);

            ConfigService.Save();
            ApplyWindowSize(Settings.StartupWidth, Settings.StartupHeight);
        }

        private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingFont) return;

            if (sender is ComboBox cb && cb.SelectedItem is string selected)
            {
                Settings.FontFamily = selected;
                TypographyService.ApplyTypography(Settings);
                ConfigService.Save();
            }
        }

        private void OnFontSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (_isInitializingFont) return;

            var newSize = Math.Clamp(e.NewValue, 12, 20);
            Settings.BaseFontSize = newSize;

            TypographyService.ApplyTypography(Settings);
            ConfigService.Save();
        }

        private static double ClampWidth(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 1200;
            return Math.Clamp(value, 640, 3840);
        }

        private static double ClampHeight(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 800;
            return Math.Clamp(value, 480, 2160);
        }

        private static void ApplyWindowSize(double width, double height)
        {
            if (App._window == null) return;

            var clampedWidth = ClampWidth(width);
            var clampedHeight = ClampHeight(height);

            try
            {
                App._window.AppWindow?.Resize(new SizeInt32((int)Math.Round(clampedWidth), (int)Math.Round(clampedHeight)));
            }
            catch
            {
                
            }

            
        }
    }
}