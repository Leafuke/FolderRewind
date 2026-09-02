using FolderRewind.Models;
using System.Collections.ObjectModel;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    public int CompressionLevel { get => _archive.CompressionLevel; set => _archive.CompressionLevel = value; }
    public int KeepCount { get => _archive.KeepCount; set => _archive.KeepCount = value; }
    public int MaxSmartBackupsPerFull { get => _archive.MaxSmartBackupsPerFull; set => _archive.MaxSmartBackupsPerFull = value; }
    public bool SkipIfUnchanged { get => _archive.SkipIfUnchanged; set => _archive.SkipIfUnchanged = value; }
    public bool FileTypeHandlingEnabled { get => _archive.FileTypeHandlingEnabled; set => _archive.FileTypeHandlingEnabled = value; }
    public ObservableCollection<FileTypeRule> FileTypeRules => _archive.FileTypeRules;
    public bool BackupBeforeRestore { get => _archive.BackupBeforeRestore; set => _archive.BackupBeforeRestore = value; }
    public bool SafeRestoreEnabled { get => _archive.SafeRestoreEnabled; set => _archive.SafeRestoreEnabled = value; }
    public bool VerifyArchiveBeforeRestore { get => _archive.VerifyArchiveBeforeRestore; set => _archive.VerifyArchiveBeforeRestore = value; }

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

    public ObservableCollection<string> RestoreWhitelist => _filters.RestoreWhitelist;
    public ObservableCollection<string> Blacklist => _filters.Blacklist;
    public ObservableCollection<string> BackupWhitelist => _filters.BackupWhitelist;
}
