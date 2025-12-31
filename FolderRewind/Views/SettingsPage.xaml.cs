using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Graphics;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page
    {
        public GlobalSettings Settings => ConfigService.CurrentConfig.GlobalSettings;

        public ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins => PluginService.InstalledPlugins;

        private bool _isInitializingLanguage;
        private bool _isInitializingFont;

        public ObservableCollection<string> FontFamilies { get; } = new()
        {
            "Segoe UI Variable",
            "Segoe UI",
            "Arial",
            "Calibri",
            "Consolas",
            "Sitka Text"
        };

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
            }
            finally
            {
                _isInitializingLanguage = false;
                _isInitializingFont = false;
            }
        }

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

            var dialog = new ContentDialog
            {
                Title = "插件商店",
                CloseButtonText = "关闭",
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

            var confirm = new ContentDialog
            {
                Title = "卸载插件",
                Content = $"确定卸载插件：{plugin.Name} ({plugin.Id})？\n\n提示：如果插件文件被占用，可能需要关闭应用后再卸载。",
                PrimaryButtonText = "卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var result = PluginService.Uninstall(plugin.Id);

            var msg = new ContentDialog
            {
                Title = result.Success ? "完成" : "失败",
                Content = result.Message,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await msg.ShowAsync();

            PluginService.RefreshInstalledList();
            Bindings.Update();
        }

        private async void OnPluginSettingsClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var defs = PluginService.GetSettingsDefinitions(plugin.Id);
            if (defs == null || defs.Count == 0)
            {
                var noSettings = new ContentDialog
                {
                    Title = "插件设置",
                    Content = "该插件未提供可配置项。",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
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
                        var tb = new TextBox { Text = initial, PlaceholderText = def.IsRequired ? "必填" : string.Empty };
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
                Title = $"插件设置 - {plugin.Name}",
                Content = scroll,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

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
                        validation.Text = $"请填写必填项：{def.DisplayName ?? def.Key}";
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
            if (sender is ToggleSwitch ts)
            {
                // 确保在保存前将实际开关值写回设置对象
                Settings.CheckForUpdates = ts.IsOn;
            }

            ConfigService.Save();
        }

        // 开机自启特殊处理
        private void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                var desired = ts.IsOn;
                var success = StartupService.SetStartup(desired);

                Settings.RunOnStartup = success && desired;

                if (!success && desired)
                {
                    ts.IsOn = false;
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
                EnableDebugLogs = Settings.EnableDebugLogs,
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
                // ignore and fall back to managed size
            }

            
        }
    }
}