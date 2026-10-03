using FolderRewind.Models;
using FolderRewind.ViewModels;
using FolderRewind.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views;

public sealed partial class ConfigSettingsPage
{
    private bool _filterPickerOpen;

    private async void OnBrowseFilterFilesClick(object sender, RoutedEventArgs e)
        => await BrowseFilterPathsAsync(sender, folders: false);

    private async void OnBrowseFilterFolderClick(object sender, RoutedEventArgs e)
        => await BrowseFilterPathsAsync(sender, folders: true);

    private async Task BrowseFilterPathsAsync(object sender, bool folders)
    {
        if (_filterPickerOpen || XamlRoot is null || sender is not FrameworkElement { Tag: string kind }
            || !Enum.TryParse<ConfigSettingsEdit>(kind, out var edit)) return;
        _filterPickerOpen = true;
        SetFilterBrowseEnabled(false);
        var draft = Config;
        try
        {
            var paths = await MainWindowService.PickRulePathsAsync(
                XamlRoot.ContentIslandEnvironment.AppWindowId, folders, $"Filter_{kind}_{folders}");
            if (!IsLoaded || !ReferenceEquals(Config, draft)) return;
            foreach (var path in paths)
                ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(edit, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))));
        }
        catch (Exception ex)
        {
            LogService.LogError(I18n.Format("Picker_Log_OpenFailed", ex.Message), nameof(ConfigSettingsPage), ex);
            NotificationService.ShowError(I18n.Format("Picker_OpenFailedWithReason", ex.Message));
        }
        finally
        {
            _filterPickerOpen = false;
            SetFilterBrowseEnabled(true);
        }
    }

    private void SetFilterBrowseEnabled(bool enabled)
    {
        if (BlacklistBrowseButton is not null) BlacklistBrowseButton.IsEnabled = enabled;
        if (BackupWhitelistBrowseButton is not null) BackupWhitelistBrowseButton.IsEnabled = enabled;
        if (RestoreWhitelistBrowseButton is not null) RestoreWhitelistBrowseButton.IsEnabled = enabled;
    }

    private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
    {
        ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.AddBlacklist, BlacklistBox.Text));
        BlacklistBox.Text = string.Empty;
    }
    private void OnRemoveBlacklistClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string item })
            ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.RemoveBlacklist, item));
    }

    private void OnAddBackupWhitelistClick(object sender, RoutedEventArgs e)
    {
        ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.AddBackupWhitelist, BackupWhitelistBox.Text));
        BackupWhitelistBox.Text = string.Empty;
    }
    private void OnRemoveBackupWhitelistClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string item })
            ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.RemoveBackupWhitelist, item));
    }

    private void OnAddRestoreWhitelistClick(object sender, RoutedEventArgs e)
    {
        ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.AddRestoreWhitelist, RestoreWhitelistBox.Text));
        RestoreWhitelistBox.Text = string.Empty;
    }
    private void OnRemoveRestoreWhitelistClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string item })
            ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.RemoveRestoreWhitelist, item));
    }

    private void OnIconSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconGrid.SelectedItem is string glyph)
            ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.SetIcon, glyph));
    }
    private void OnAddFileTypeRuleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.AddFileTypeRule, FileTypePatternBox.Text, FileTypeLevelBox.Value));
        FileTypePatternBox.Text = string.Empty;
        FileTypeLevelBox.Value = 1;
    }
    private void OnRemoveFileTypeRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileTypeRule rule })
            ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.RemoveFileTypeRule, rule));
    }
}
