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
        OnPropertyChanged(nameof(CanCleanNow));
        OnPropertyChanged(nameof(CleanNowLabel));
    }
    internal async Task SaveForCleanupAsync(CancellationToken token)
    {
        string? error = null;
        LastSaveSucceeded = await _saveController.SaveAsync(ValidateForSave,
            (_editingDraft is not null ? () => _editingDraft.CommitAsync(token) : SaveDraft ?? (() => ConfigService.SaveAsync())), message => error = message, token);
        RefreshCleanupActions();
        token.ThrowIfCancellationRequested();
        if (!LastSaveSucceeded) throw new InvalidOperationException(error ?? I18n.GetString("Common_Failed"));
    }
}
