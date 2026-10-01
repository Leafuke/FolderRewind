using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class ConfigSettingsPage
{
    private void ExecuteAction(ConfigSettingsAction action)
    {
        if (ViewModel.ActionCommand.CanExecute(action)) ViewModel.ActionCommand.Execute(action);
    }

    private async Task ShowChildAndRestoreAsync(ConfigSettingsAction action)
    {
        if (_showingChild || !ViewModel.ActionCommand.CanExecute(action)) return;
        _showingChild = true;
        await Task.Yield();
        try { await ViewModel.ActionCommand.ExecuteAsync(action); }
        finally
        {
            _showingChild = false;
            ViewModel.RefreshCloudUi();
            Bindings.Update();
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
        => ExecuteAction(ConfigSettingsAction.Browse);

    private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        => ExecuteAction(ConfigSettingsAction.OpenDestination);

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
        => ExecuteAction(ConfigSettingsAction.OpenConfigFolder);

    private void OnOpenConfigFileClick(object sender, RoutedEventArgs e)
        => ExecuteAction(ConfigSettingsAction.OpenConfigFile);

    private void OnSaveAsTemplateClick(object sender, RoutedEventArgs e)
        => TaskObserver.Observe(ShowChildAndRestoreAsync(ConfigSettingsAction.SaveAsTemplate), nameof(ConfigSettingsPage));

    private void OnBrowseCloudExecutableClick(object sender, RoutedEventArgs e)
        => ExecuteAction(ConfigSettingsAction.BrowseCloudExecutable);

    private void OnBrowseCloudWorkingDirectoryClick(object sender, RoutedEventArgs e)
        => ExecuteAction(ConfigSettingsAction.BrowseCloudWorkingDirectory);

    private void OnOpenCloudSyncClick(object sender, RoutedEventArgs e)
        => TaskObserver.Observe(ShowChildAndRestoreAsync(ConfigSettingsAction.OpenCloudSync), nameof(ConfigSettingsPage));

    private void OnApplyCloudTemplateClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyCloudTemplate();
        UpdateCloudBindings();
    }
}
