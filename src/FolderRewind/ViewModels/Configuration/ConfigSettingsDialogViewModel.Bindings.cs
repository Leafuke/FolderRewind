using FolderRewind.Models;
using System.Collections.ObjectModel;
using FolderRewind.Plugin.Abstractions;
using System;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private static readonly string[] CompressionMethods = { "LZMA2", "Deflate", "BZip2", "zstd" };
    public int CompressionMethodSelectedIndex
    {
        get => Math.Max(0, Array.IndexOf(CompressionMethods, _archive.Method));
        set { if (value >= 0 && value < CompressionMethods.Length) _archive.Method = CompressionMethods[value]; }
    }

    public int CompressionLevel { get => _archive.CompressionLevel; set => _archive.CompressionLevel = value; }
    public int KeepCount { get => _archive.KeepCount; set => _archive.KeepCount = value; }
    public int MaxSmartBackupsPerFull { get => _archive.MaxSmartBackupsPerFull; set => _archive.MaxSmartBackupsPerFull = value; }
    public bool SkipIfUnchanged { get => _archive.SkipIfUnchanged; set => _archive.SkipIfUnchanged = value; }
    public int SourceReadModeSelectedIndex
    {
        get => (int)_archive.SourceReadMode;
        set { if (value is >= 0 and <= 2) { _archive.SourceReadMode = (BackupSourceReadMode)value; OnPropertyChanged(); } }
    }
    public bool FileTypeHandlingEnabled { get => _archive.FileTypeHandlingEnabled; set => _archive.FileTypeHandlingEnabled = value; }
    public ObservableCollection<FileTypeRule> FileTypeRules => _archive.FileTypeRules;
    public bool BackupBeforeRestore { get => _archive.BackupBeforeRestore; set => _archive.BackupBeforeRestore = value; }
    public bool QuickRestoreSafetySnapshotEnabled { get => _archive.QuickRestoreSafetySnapshotEnabled; set => _archive.QuickRestoreSafetySnapshotEnabled = value; }
    public int BackupVerificationSelectedIndex
    {
        get => (int)_archive.BackupVerificationLevel;
        set { if (value is 0 or 1) { _archive.BackupVerificationLevel = (BackupArchiveVerificationLevel)value; OnPropertyChanged(); } }
    }
    public int RestorePreflightSelectedIndex
    {
        get => (int)_archive.RestorePreflightMode;
        set { if (value is 0 or 1) { _archive.RestorePreflightMode = (RestoreArchivePreflightMode)value; OnPropertyChanged(); } }
    }

    public bool AutoBackupEnabled { get => _automation.AutoBackupEnabled; set => _automation.AutoBackupEnabled = value; }
    public bool RunOnAppStart { get => _automation.RunOnAppStart; set => _automation.RunOnAppStart = value; }
    public bool IntervalMode { get => _automation.IntervalMode; set => _automation.IntervalMode = value; }
    public int IntervalMinutes { get => _automation.IntervalMinutes; set => _automation.IntervalMinutes = value; }
    public bool ScheduledMode { get => _automation.ScheduledMode; set => _automation.ScheduledMode = value; }
    public ObservableCollection<ScheduleEntry> ScheduleEntries => _automation.ScheduleEntries;
    public bool StopAfterNoChangeEnabled { get => _automation.StopAfterNoChangeEnabled; set => _automation.StopAfterNoChangeEnabled = value; }
    public int StopAfterNoChangeCount { get => _automation.StopAfterNoChangeCount; set => _automation.StopAfterNoChangeCount = value; }

    public bool UseRegex
    {
        get => _filters.UseRegex;
        set
        {
            if (_filters.UseRegex == value) return;
            _filters.UseRegex = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<PathFilterRule> RestoreWhitelist => _filters.RestoreWhitelist;
    public ObservableCollection<PathFilterRule> Blacklist => _filters.Blacklist;
    public ObservableCollection<PathFilterRule> BackupWhitelist => _filters.BackupWhitelist;
}
