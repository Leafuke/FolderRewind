using FolderRewind.Services;
using System;
using System.Linq;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private ConfigSettingsDraftService? _editingDraft;
    internal bool HasUnsavedChanges => _editingDraft?.IsDirty == true;
    internal void BeginDraft(string configId)
    {
        var original = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == configId)
            ?? throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        _editingDraft = new(original);
        Rebind(_editingDraft.Draft);
        SaveDraft = _editingDraft.CommitAsync;
    }
    internal void AcceptDraftNormalization() => _editingDraft?.AcceptInitialNormalization();
}
