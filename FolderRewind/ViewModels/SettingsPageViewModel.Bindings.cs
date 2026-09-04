using FolderRewind.Models;
using System;
using System.ComponentModel;

namespace FolderRewind.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private PluginHostSettings? _observedPluginSettings;

    public int ThemeIndex { get => Settings.ThemeIndex; set => SetSetting(Settings.ThemeIndex, value, item => Settings.ThemeIndex = item); }
    public int CompletionSoundIndex { get => Settings.CompletionSoundIndex; set => SetSetting(Settings.CompletionSoundIndex, value, item => Settings.CompletionSoundIndex = item); }
    public double BaseFontSize { get => Settings.BaseFontSize; set => SetSetting(Settings.BaseFontSize, value, item => Settings.BaseFontSize = item); }
    public bool UseHistoryStatusColors { get => Settings.UseHistoryStatusColors; set => SetSetting(Settings.UseHistoryStatusColors, value, item => Settings.UseHistoryStatusColors = item); }
    public double NavPaneWidth { get => Settings.NavPaneWidth; set => SetSetting(Settings.NavPaneWidth, value, item => Settings.NavPaneWidth = item); }
    public double StartupWidth { get => Settings.StartupWidth; set => SetSetting(Settings.StartupWidth, value, item => Settings.StartupWidth = item); }
    public double StartupHeight { get => Settings.StartupHeight; set => SetSetting(Settings.StartupHeight, value, item => Settings.StartupHeight = item); }
    public int SponsorAccentColorIndex { get => Settings.SponsorAccentColorIndex; set => SetSetting(Settings.SponsorAccentColorIndex, value, item => Settings.SponsorAccentColorIndex = item); }
    public int SponsorBackdropIndex { get => Settings.SponsorBackdropIndex; set => SetSetting(Settings.SponsorBackdropIndex, value, item => Settings.SponsorBackdropIndex = item); }
    public string SponsorTitleText { get => Settings.SponsorTitleText; set => SetSetting(Settings.SponsorTitleText, value ?? string.Empty, item => Settings.SponsorTitleText = item); }
    public string SponsorTitleIconGlyph { get => Settings.SponsorTitleIconGlyph; set => SetSetting(Settings.SponsorTitleIconGlyph, value ?? string.Empty, item => Settings.SponsorTitleIconGlyph = item); }
    public bool SponsorBackgroundEnabled { get => Settings.SponsorBackgroundEnabled; set => SetSetting(Settings.SponsorBackgroundEnabled, value, item => Settings.SponsorBackgroundEnabled = item); }
    public int SponsorBackgroundStretchIndex { get => Settings.SponsorBackgroundStretchIndex; set => SetSetting(Settings.SponsorBackgroundStretchIndex, value, item => Settings.SponsorBackgroundStretchIndex = item); }
    public bool RunOnStartup { get => Settings.RunOnStartup; set => SetSetting(Settings.RunOnStartup, value, item => Settings.RunOnStartup = item); }
    public bool SilentStartup { get => Settings.SilentStartup; set => SetSetting(Settings.SilentStartup, value, item => Settings.SilentStartup = item); }
    public bool EnableNotifications { get => Settings.EnableNotifications; set => SetSetting(Settings.EnableNotifications, value, item => Settings.EnableNotifications = item); }
    public int ToastNotificationLevel { get => Settings.ToastNotificationLevel; set => SetSetting(Settings.ToastNotificationLevel, value, item => Settings.ToastNotificationLevel = item); }
    public bool EnableNotices { get => Settings.EnableNotices; set => SetSetting(Settings.EnableNotices, value, item => Settings.EnableNotices = item); }
    public bool EnableUpdateReminder { get => Settings.EnableUpdateReminder; set => SetSetting(Settings.EnableUpdateReminder, value, item => Settings.EnableUpdateReminder = item); }
    public int FileSizeWarningThresholdKB { get => Settings.FileSizeWarningThresholdKB; set => SetSetting(Settings.FileSizeWarningThresholdKB, value, item => Settings.FileSizeWarningThresholdKB = item); }
    public bool EnableFileLogging { get => Settings.EnableFileLogging; set => SetSetting(Settings.EnableFileLogging, value, item => Settings.EnableFileLogging = item); }
    public int MaxLogFileSizeMb { get => Settings.MaxLogFileSizeMb; set => SetSetting(Settings.MaxLogFileSizeMb, value, item => Settings.MaxLogFileSizeMb = item); }
    public int LogRetentionDays { get => Settings.LogRetentionDays; set => SetSetting(Settings.LogRetentionDays, value, item => Settings.LogRetentionDays = item); }
    public bool PluginsAutoCheckUpdates { get => Settings.Plugins.AutoCheckUpdates; set => SetSetting(Settings.Plugins.AutoCheckUpdates, value, item => Settings.Plugins.AutoCheckUpdates = item); }
    public bool EnableKnotLink { get => Settings.EnableKnotLink; set => SetSetting(Settings.EnableKnotLink, value, item => Settings.EnableKnotLink = item); }
    public bool AutoStartKnotLinkServer { get => Settings.AutoStartKnotLinkServer; set => SetSetting(Settings.AutoStartKnotLinkServer, value, item => Settings.AutoStartKnotLinkServer = item); }
    public string SevenZipPath { get => Settings.SevenZipPath; set => SetSetting(Settings.SevenZipPath, value ?? string.Empty, item => Settings.SevenZipPath = item); }
    public string DefaultBackupRootPath { get => Settings.DefaultBackupRootPath; set => SetSetting(Settings.DefaultBackupRootPath, value ?? string.Empty, item => Settings.DefaultBackupRootPath = item); }
    public string RcloneExecutablePath { get => Settings.RcloneExecutablePath; set => SetSetting(Settings.RcloneExecutablePath, value ?? string.Empty, item => Settings.RcloneExecutablePath = item); }
    public string DefaultCloudRemoteBasePath { get => Settings.DefaultCloudRemoteBasePath; set => SetSetting(Settings.DefaultCloudRemoteBasePath, value ?? string.Empty, item => Settings.DefaultCloudRemoteBasePath = item); }
    public bool AutoDownloadMissingCloudBackupsBeforeRestore { get => Settings.AutoDownloadMissingCloudBackupsBeforeRestore; set => SetSetting(Settings.AutoDownloadMissingCloudBackupsBeforeRestore, value, item => Settings.AutoDownloadMissingCloudBackupsBeforeRestore = item); }
    public int AppUpdatePreferredSource { get => Settings.AppUpdatePreferredSource; set => SetSetting(Settings.AppUpdatePreferredSource, value, item => Settings.AppUpdatePreferredSource = item); }
    public bool AppUpdateAutoFallback { get => Settings.AppUpdateAutoFallback; set => SetSetting(Settings.AppUpdateAutoFallback, value, item => Settings.AppUpdateAutoFallback = item); }
    public string AppUpdateCustomMirrorUrl { get => Settings.AppUpdateCustomMirrorUrl; set => SetSetting(Settings.AppUpdateCustomMirrorUrl, value ?? string.Empty, item => Settings.AppUpdateCustomMirrorUrl = item); }

    private void ObserveBindableSettings()
    {
        Settings.PropertyChanged += OnBindableSettingsPropertyChanged;
        ObservePluginSettings();
    }

    private void StopObservingBindableSettings()
    {
        Settings.PropertyChanged -= OnBindableSettingsPropertyChanged;
        if (_observedPluginSettings is not null)
            _observedPluginSettings.PropertyChanged -= OnBindablePluginSettingsPropertyChanged;
        _observedPluginSettings = null;
    }

    private void OnBindableSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlobalSettings.Plugins))
            ObservePluginSettings();
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }

    private void ObservePluginSettings()
    {
        if (ReferenceEquals(_observedPluginSettings, Settings.Plugins)) return;
        if (_observedPluginSettings is not null)
            _observedPluginSettings.PropertyChanged -= OnBindablePluginSettingsPropertyChanged;
        _observedPluginSettings = Settings.Plugins;
        _observedPluginSettings.PropertyChanged += OnBindablePluginSettingsPropertyChanged;
        OnPropertyChanged(nameof(PluginsAutoCheckUpdates));
    }

    private void OnBindablePluginSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) || e.PropertyName == nameof(PluginHostSettings.AutoCheckUpdates))
            OnPropertyChanged(nameof(PluginsAutoCheckUpdates));
    }

    private void SetSetting<T>(T current, T value, Action<T> apply)
    {
        if (Equals(current, value)) return;
        apply(value);
        _isDirty = true;
    }
}
