using FolderRewind.Models;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal interface IFolderManagerInteractionService
{
    Task MessageAsync(string title, string message, CancellationToken token);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken token, bool destructive = false);
    Task<string?> PickFolderAsync(string title, string identifier, CancellationToken token);
    Task<string?> PickIconAsync(CancellationToken token);
    Task<string?> RequestRenameAsync(ManagedFolder folder, CancellationToken token);
    Task<BackupSourceScope?> EditScopeAsync(BackupConfig config, ManagedFolder folder, CancellationToken token);
    Task ShowDetailsAsync(BackupConfig config, ManagedFolder folder, CancellationToken token);
    Task ShowConfigSettingsAsync(BackupConfig config, CancellationToken token);
    void Notify(string message, bool error = false);
}

internal sealed class FolderManagerInteractionService(Func<XamlRoot?> root) : IFolderManagerInteractionService
{
    public Task MessageAsync(string title, string message, CancellationToken token)
        => AppDialogService.Default.ShowMessageAsync(title, message, root(), cancellationToken: token);
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token, bool destructive = false)
        => AppDialogService.Default.ConfirmAsync(title, message, I18n.GetString("Common_Confirm"), root(), destructive, token);
    public async Task<string?> PickFolderAsync(string title, string identifier, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = await MainWindowService.PickFolderPathAsync(title, identifier, MainWindowService.SuggestedPickerLocation.ComputerFolder);
        token.ThrowIfCancellationRequested();
        return path;
    }
    public async Task<string?> PickIconAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = await MainWindowService.PickFilePathAsync(I18n.GetString("FolderManager_ChangeCoverPickerTitle"),
            "FolderRewind.FolderManager.ChangeCover", new[] { ".png", ".jpg", ".jpeg" },
            MainWindowService.SuggestedPickerLocation.PicturesLibrary, viewMode: Windows.Storage.Pickers.PickerViewMode.Thumbnail);
        token.ThrowIfCancellationRequested();
        return path;
    }
    public async Task<string?> RequestRenameAsync(ManagedFolder folder, CancellationToken token)
    {
        var dialog = new FolderRenameDialog();
        var leaf = Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        dialog.Initialize(folder, FolderRenameService.PreviewRename(folder, leaf));
        return await AppDialogService.Default.ShowCustomAsync(dialog, root(), token) == ContentDialogResult.Primary
            ? dialog.ViewModel.NewLeafName : null;
    }
    public async Task<BackupSourceScope?> EditScopeAsync(BackupConfig config, ManagedFolder folder, CancellationToken token)
    {
        var dialog = new SourceScopeEditorDialog(config, folder);
        return await AppDialogService.Default.ShowCustomAsync(dialog, root(), token) == ContentDialogResult.Primary ? dialog.ResultScope : null;
    }
    public async Task ShowDetailsAsync(BackupConfig config, ManagedFolder folder, CancellationToken token)
    {
        var dialog = new FolderDetailsDialog();
        var loading = dialog.InitializeAsync(config, folder);
        TaskObserver.Observe(loading, nameof(FolderDetailsDialog));
        await AppDialogService.Default.ShowCustomAsync(dialog, root(), token);
        await loading;
    }
    public async Task ShowConfigSettingsAsync(BackupConfig config, CancellationToken token)
    {
        var dialog = ConfigSettingsDialog.Instance;
        dialog.Rebind(config);
        await AppDialogService.Default.ShowCustomAsync(dialog, root(), token);
    }
    public void Notify(string message, bool error = false)
    {
        if (error) NotificationService.ShowError(message);
        else NotificationService.ShowSuccess(message);
    }
}
