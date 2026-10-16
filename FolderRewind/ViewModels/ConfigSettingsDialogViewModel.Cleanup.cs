using FolderRewind.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    public bool CanCleanNow => KeepCount > 0;
    public string CleanNowLabel => I18n.GetString(HasUnsavedChanges ? "Retention_SaveClean" : "Retention_CleanNow");
    internal void RefreshCleanupActions()
    {
        RefreshSemanticAvailability();
        OnPropertyChanged(nameof(CanCleanNow));
        OnPropertyChanged(nameof(CleanNowLabel));
    }
    internal async Task SaveForCleanupAsync(CancellationToken token)
    {
        string? error = null;
        _savingDraft = true; RefreshDraftState();
        try
        {
            LastSaveSucceeded = await _saveController.SaveAsync(ValidateForSave,
                (_editingDraft is not null ? () => _editingDraft.CommitAsync(token) : SaveDraft ?? (() => ConfigService.SaveAsync())), message => error = message, token);
            _draftSaveResult = LastSaveSucceeded;
            RefreshCleanupActions();
        }
        finally { _savingDraft = false; RefreshDraftState(); }
        token.ThrowIfCancellationRequested();
        if (!LastSaveSucceeded) throw new InvalidOperationException(error ?? I18n.GetString("Common_Failed"));
    }
}
