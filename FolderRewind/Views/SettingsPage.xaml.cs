using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage.Pickers;
using Windows.Graphics;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page
    {
        public GlobalSettings Settings => ConfigService.CurrentConfig.GlobalSettings;

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