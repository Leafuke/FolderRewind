using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page
    {
        public GlobalSettings Settings => ConfigService.CurrentConfig.GlobalSettings;

        private bool _isInitializingLanguage;

        public SettingsPage()
        {
            this.InitializeComponent();

            _isInitializingLanguage = true;
            try
            {
                if (LanguageCombo != null)
                {
                    LanguageCombo.SelectedIndex = LanguageToIndex(Settings.Language);
                }
            }
            finally
            {
                _isInitializingLanguage = false;
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
                // 先将设置写回模型，保证保存的是最新值
                Settings.RunOnStartup = ts.IsOn;
                StartupService.SetStartup(ts.IsOn);
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
    }
}