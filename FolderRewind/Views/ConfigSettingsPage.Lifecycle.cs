using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class ConfigSettingsPage
{
    private bool _allowNavigation;
    private bool _leaving;
    private void OnCloudSetup(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("CloudSetup", new ConfigSettingsNavigationParameter(Config.Id));
    private void OnApplyPerformance(object sender, RoutedEventArgs e) => ViewModel.ApplyPendingPerformance();
    private void OnUndoPerformance(object sender, RoutedEventArgs e) => ViewModel.UndoPerformance();
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = (sender as TextBox)?.Text?.Trim();
        if (string.IsNullOrEmpty(query) || !_isDialogReady) return;
        var group = new[] { "General", "Resource", "Backup", "Restore", "Automation", "Cloud", "Filter" }
            .FirstOrDefault(g => I18n.GetString("SettingsProject_Search" + g).Split('|').Any(word => word.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        if (group is null) return;
        ConfigSelectorBar.SelectedItem = ConfigSelectorBar.Items.FirstOrDefault(i => i.Tag as string == group);
        DraftStatus.Text = I18n.Format("SettingsProject_SearchResult", I18n.GetString("SettingsProject_Group" + group));
    }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var request = e.Parameter as ConfigSettingsNavigationParameter;
        try { ViewModel.BeginDraft(request?.ConfigId ?? ""); }
        catch { NavigationService.NavigateTo("Home"); return; }
        Rebind(ViewModel.Config);
        ViewModel.ActivateActions();
        ConfigSelectorBar.SelectedItem = ConfigSelectorBar.Items.FirstOrDefault(i => string.Equals(i.Tag as string, request?.Group, StringComparison.Ordinal)) ?? ConfigSelectorBar.Items[0];
        ViewModel.AcceptDraftNormalization();
        DraftStatus.Text = I18n.GetString("SettingsProject_DraftHelp");
    }
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (_allowNavigation || !ViewModel.HasUnsavedChanges) return;
        e.Cancel = true;
        if (_leaving) return;
        _leaving = true;
        TaskObserver.Observe(ConfirmLeaveAsync(e.SourcePageType, e.Parameter), nameof(ConfigSettingsPage));
    }
    private async Task ConfirmLeaveAsync(Type destination, object? parameter)
    {
        try
        {
            var dialog = new ContentDialog { Title = I18n.GetString("SettingsProject_Unsaved"), Content = I18n.GetString("SettingsProject_UnsavedBody"),
                PrimaryButtonText = I18n.GetString("SettingsProject_SaveAction"), SecondaryButtonText = I18n.GetString("SettingsProject_Discard"), CloseButtonText = I18n.GetString("Common_Cancel") };
            var answer = await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot);
            if (answer == ContentDialogResult.None) return;
            if (answer == ContentDialogResult.Primary)
            {
                await ViewModel.SaveCommand.ExecuteAsync(null);
                if (!ViewModel.LastSaveSucceeded) return;
            }
            _allowNavigation = true;
            Frame.Navigate(destination, parameter);
        }
        finally { _leaving = false; }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelActions();
        ViewModel.Unbind();
        Config.PropertyChanged -= OnDialogConfigPropertyChanged;
        Config.Cloud.PropertyChanged -= OnDialogCloudPropertyChanged;
        base.OnNavigatedFrom(e);
    }
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.SaveCommand.CanExecute(null)) return;
        IsEnabled = false;
        try
        {
            await ViewModel.SaveCommand.ExecuteAsync(null);
            DraftStatus.Text = I18n.GetString(ViewModel.LastSaveSucceeded ? "SettingsProject_Saved" : "SettingsProject_SaveFailed");
        }
        finally { IsEnabled = true; }
    }
    private void OnCancelClick(object sender, RoutedEventArgs e)
        => NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(Config.Id));
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.DeleteCommand.CanExecute(null)) return;
        await ViewModel.DeleteCommand.ExecuteAsync(null);
        if (ViewModel.LastDeleteSucceeded) { _allowNavigation = true; NavigationService.NavigateTo("Home"); }
    }
}
