using FolderRewind.Models;
using System;
using FolderRewind.Services;
using System.Collections.ObjectModel;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.ViewModels;

internal enum ConfigSettingsEdit
{
    AddBlacklist, RemoveBlacklist, AddBackupWhitelist, RemoveBackupWhitelist,
    AddRestoreWhitelist, RemoveRestoreWhitelist, SetIcon, AddFileTypeRule, RemoveFileTypeRule,
    AddSchedule, RemoveSchedule, ChangeFilterTarget
}

internal sealed record ConfigSettingsEditRequest(ConfigSettingsEdit Kind, object? Value = null, double Number = 1);
internal sealed record FilterTargetEdit(string List, PathFilterRule Rule, PathFilterTarget Target);

public sealed partial class ConfigSettingsDialogViewModel
{
    private void ApplyDraftEdit(ConfigSettingsEditRequest? request)
    {
        if (request is null) return;
        var text = request.Value as string;
        switch (request.Kind)
        {
            case ConfigSettingsEdit.AddBlacklist: AddRule(_filters.Blacklist, request.Value); break;
            case ConfigSettingsEdit.RemoveBlacklist: if (request.Value is PathFilterRule blacklist) _filters.Blacklist.Remove(blacklist); break;
            case ConfigSettingsEdit.AddBackupWhitelist: AddRule(_filters.BackupWhitelist, request.Value); break;
            case ConfigSettingsEdit.RemoveBackupWhitelist: if (request.Value is PathFilterRule whitelist) _filters.BackupWhitelist.Remove(whitelist); break;
            case ConfigSettingsEdit.AddRestoreWhitelist: AddRule(_filters.RestoreWhitelist, request.Value); break;
            case ConfigSettingsEdit.RemoveRestoreWhitelist: if (request.Value is PathFilterRule restore) _filters.RestoreWhitelist.Remove(restore); break;
            case ConfigSettingsEdit.ChangeFilterTarget:
                if (request.Value is FilterTargetEdit edit)
                {
                    var rules = edit.List switch
                    {
                        nameof(FilterSettings.Blacklist) => _filters.Blacklist,
                        nameof(FilterSettings.BackupWhitelist) => _filters.BackupWhitelist,
                        nameof(FilterSettings.RestoreWhitelist) => _filters.RestoreWhitelist,
                        _ => throw new ArgumentException("Unknown filter list.")
                    };
                    var index = rules.IndexOf(edit.Rule);
                    if (index >= 0) rules[index] = edit.Rule with { Target = edit.Target, SemanticsVersion = 2 };
                }
                break;
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
        RefreshDraftState(edited: true);
    }

    private static void AddRule(ObservableCollection<PathFilterRule> rules, object? value)
    {
        if (value is PathFilterRule rule) BackupFilterRulePolicy.AddDistinct(rules, rule);
        else if (value is string text && !string.IsNullOrWhiteSpace(text)) BackupFilterRulePolicy.AddDistinct(rules, new PathFilterRule(text, PathFilterTarget.File));
    }
}
