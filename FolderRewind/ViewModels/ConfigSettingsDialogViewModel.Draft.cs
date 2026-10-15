using FolderRewind.Services;
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private ConfigSettingsDraftService? _editingDraft;
    private readonly List<Action> _draftUnsubscribe = [];
    private bool _savingDraft;
    private bool? _draftSaveResult;
    public bool HasUnsavedChanges => _editingDraft?.IsDirty == true;
    public string DraftStatusText => I18n.GetString(_savingDraft ? "SettingsProject_Saving"
        : _draftSaveResult == false ? "SettingsProject_SaveFailed"
        : HasUnsavedChanges ? "SettingsProject_Modified"
        : _draftSaveResult == true ? "SettingsProject_Saved" : "SettingsProject_DraftHelp");

    internal void RefreshDraftState(bool edited = false)
    {
        if (edited && !_savingDraft) _draftSaveResult = null;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DraftStatusText));
    }

    private void StopObservingDraft()
    {
        foreach (var unsubscribe in _draftUnsubscribe) unsubscribe();
        _draftUnsubscribe.Clear();
    }

    private void ObserveDraft()
    {
        StopObservingDraft();
        if (_editingDraft is null) return;
        var values = new object?[] { _config, _archive, _automation, _filters, _cloud, _config.BackupScope,
            _config.SourceFolders, _archive.FileTypeRules, _automation.ScheduleEntries,
            _filters.Blacklist, _filters.BackupWhitelist, _filters.RestoreWhitelist };
        void ObserveValue(object? value)
        {
            if (value is INotifyPropertyChanged properties)
            {
                PropertyChangedEventHandler handler = (_, _) => RefreshDraftState(edited: true);
                properties.PropertyChanged += handler;
                _draftUnsubscribe.Add(() => properties.PropertyChanged -= handler);
            }
            if (value is INotifyCollectionChanged collection)
            {
                NotifyCollectionChangedEventHandler handler = (_, _) => { ObserveDraft(); RefreshDraftState(edited: true); };
                collection.CollectionChanged += handler;
                _draftUnsubscribe.Add(() => collection.CollectionChanged -= handler);
            }
        }
        foreach (var value in values)
        {
            ObserveValue(value);
            if (value is IEnumerable items)
                foreach (var item in items) ObserveValue(item);
        }
    }
    internal void BeginDraft(string configId)
    {
        var original = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == configId)
            ?? throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        _editingDraft = new(original);
        Rebind(_editingDraft.Draft);
        SaveDraft = _editingDraft.CommitAsync;
        _draftSaveResult = null;
        RefreshDraftState();
    }
    internal void AcceptDraftNormalization()
    {
        _editingDraft?.AcceptInitialNormalization();
        RefreshDraftState();
    }
}
