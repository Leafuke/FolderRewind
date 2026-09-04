using FolderRewind.Models;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views;

public sealed partial class ConfigSettingsDialog
{
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
