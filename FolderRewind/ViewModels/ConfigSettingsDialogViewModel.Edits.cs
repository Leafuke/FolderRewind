using FolderRewind.Models;
using System;
using System.Collections.ObjectModel;

namespace FolderRewind.ViewModels;

internal enum ConfigSettingsEdit
{
    AddBlacklist, RemoveBlacklist, AddBackupWhitelist, RemoveBackupWhitelist,
    AddRestoreWhitelist, RemoveRestoreWhitelist, SetIcon, AddFileTypeRule, RemoveFileTypeRule,
    AddSchedule, RemoveSchedule
}

internal sealed record ConfigSettingsEditRequest(ConfigSettingsEdit Kind, object? Value = null, double Number = 1);

public sealed partial class ConfigSettingsDialogViewModel
{
    private void ApplyDraftEdit(ConfigSettingsEditRequest? request)
    {
        if (request is null) return;
        var text = request.Value as string;
        switch (request.Kind)
        {
            case ConfigSettingsEdit.AddBlacklist: AddRule(_filters.Blacklist, text); break;
            case ConfigSettingsEdit.RemoveBlacklist: if (text is not null) _filters.Blacklist.Remove(text); break;
            case ConfigSettingsEdit.AddBackupWhitelist: AddRule(_filters.BackupWhitelist, text); break;
            case ConfigSettingsEdit.RemoveBackupWhitelist: if (text is not null) _filters.BackupWhitelist.Remove(text); break;
            case ConfigSettingsEdit.AddRestoreWhitelist: AddRule(_filters.RestoreWhitelist, text); break;
            case ConfigSettingsEdit.RemoveRestoreWhitelist: if (text is not null) _filters.RestoreWhitelist.Remove(text); break;
            case ConfigSettingsEdit.SetIcon:
                if (!string.IsNullOrWhiteSpace(text)) _config.IconGlyph = text;
                break;
            case ConfigSettingsEdit.AddFileTypeRule:
                if (!string.IsNullOrWhiteSpace(text))
                    _archive.FileTypeRules.Add(new FileTypeRule
                    {
                        Pattern = text.Trim(),
                        CompressionLevel = double.IsFinite(request.Number) ? (int)Math.Clamp(request.Number, 0, 9) : 1
                    });
                break;
            case ConfigSettingsEdit.RemoveFileTypeRule:
                if (request.Value is FileTypeRule rule) _archive.FileTypeRules.Remove(rule);
                break;
            case ConfigSettingsEdit.AddSchedule:
                _automation.ScheduleEntries ??= new ObservableCollection<ScheduleEntry>();
                _automation.ScheduleEntries.Add(new ScheduleEntry { MonthSelection = 0, DaySelection = 0, Hour = 8, Minute = 0 });
                break;
            case ConfigSettingsEdit.RemoveSchedule:
                if (request.Value is ScheduleEntry entry) _automation.ScheduleEntries?.Remove(entry);
                break;
        }
    }

    private static void AddRule(ObservableCollection<string> rules, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) rules.Add(text.Trim());
    }
}
