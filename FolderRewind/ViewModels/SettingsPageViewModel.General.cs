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

            _isDirty = true;
        }

        public void HandleNotificationsToggled(bool isOn)
        {
            Settings.EnableNotifications = isOn;
            _isDirty = true;

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
            _isDirty = true;
        }

        public void HandleFileSizeWarningThresholdChanged(double newValue)
        {
            if (double.IsNaN(newValue))
            {
                return;
            }

            Settings.FileSizeWarningThresholdKB = (int)Math.Clamp(newValue, 0, 10240);
            _isDirty = true;
        }

        public void HandleAutoDownloadMissingCloudBackupsBeforeRestoreToggled(bool isOn)
        {
            Settings.AutoDownloadMissingCloudBackupsBeforeRestore = isOn;
            _isDirty = true;
        }

        public void HandleNoticesToggled(bool isOn)
        {
            Settings.EnableNotices = isOn;
            _isDirty = true;
        }

        public void HandleUpdateReminderToggled(bool isOn)
        {
            Settings.EnableUpdateReminder = isOn;
            _isDirty = true;
        }

        public void HandleAppUpdateSourceChanged(int selectedIndex)
        {
            Settings.AppUpdatePreferredSource = Math.Clamp(selectedIndex, 0, 3);
            _isDirty = true;
        }

        public void HandleAppUpdateAutoFallbackToggled(bool isOn)
        {
            Settings.AppUpdateAutoFallback = isOn;
            _isDirty = true;
        }

        public void HandleAppUpdateCustomMirrorChanged(string? customUrl)
        {
            var normalized = customUrl?.Trim() ?? string.Empty;
            if (string.Equals(Settings.AppUpdateCustomMirrorUrl, normalized, StringComparison.Ordinal))
            {
                return;
            }

            Settings.AppUpdateCustomMirrorUrl = normalized;
            _isDirty = true;
        }

        public int GetLanguageSelectedIndex()
        {
            return LanguageToIndex(Settings.Language);
        }

        public void HandleLanguageChanged(int selectedIndex)
        {
            Settings.Language = IndexToLanguage(selectedIndex);
            _isDirty = true;
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

            _isDirty = true;

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
            _isDirty = true;
            return Settings.SilentStartup;
        }

        public void ApplySevenZipPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Settings.SevenZipPath = path;
            _isDirty = true;
        }

        public void ApplyRclonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Settings.RcloneExecutablePath = path;
            _isDirty = true;
        }

        public void ApplyDefaultCloudRemoteBasePath(string path)
        {
            var normalized = path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            Settings.DefaultCloudRemoteBasePath = normalized;
            _isDirty = true;
        }

        public void ApplyDefaultBackupRootPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Settings.DefaultBackupRootPath = path;
            _isDirty = true;
        }


        public void HandleLoggingChanged(bool isOn)
        {
            Settings.EnableFileLogging = isOn;
            PushLogOptions();
            _isDirty = true;
        }

        public void HandleLogSizeChanged(double newValue)
        {
            Settings.MaxLogFileSizeMb = (int)Math.Clamp(newValue, 1, 50);
            PushLogOptions();
            _isDirty = true;
        }

        public void HandleRetentionChanged(double newValue)
        {
            Settings.LogRetentionDays = (int)Math.Clamp(newValue, 1, 60);
            PushLogOptions();
            _isDirty = true;
        }

        public void HandleHistoryColorsToggled(bool isOn)
        {
            Settings.UseHistoryStatusColors = isOn;
            _isDirty = true;
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

            _isDirty = true;
        }

        public void HandleApplyStartupSize()
        {
            Settings.StartupWidth = ClampWidth(Settings.StartupWidth);
            Settings.StartupHeight = ClampHeight(Settings.StartupHeight);

            _isDirty = true;
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
            _isDirty = true;
        }

        public void HandleFontSizeChanged(double newSize)
        {
            Settings.BaseFontSize = Math.Clamp(newSize, 12, 20);
            TypographyService.ApplyTypography(Settings);
            _isDirty = true;
        }


        private static int LanguageToIndex(string? language)
        {
            return LanguageSettingPolicy.ToSelectionIndex(language);
        }

        private static string IndexToLanguage(int index)
        {
            return LanguageSettingPolicy.FromSelectionIndex(index);
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

    }
}
