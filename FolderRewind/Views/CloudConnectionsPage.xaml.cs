using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Linq;

namespace FolderRewind.Views;

public sealed partial class CloudConnectionsPage : Page
{
    public CloudConnectionsViewModel ViewModel { get; } = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    public CloudConnectionsPage() { InitializeComponent(); Loaded += (_, _) => ViewModel.Refresh(); Unloaded += (_, _) => _lifetime.Cancel(); }
    private void OnSelected(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedProfileId = ((sender as ListView)?.SelectedItem as CloudConnectionDisplay)?.Id;
    private void OnCreateShared(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(ManageConnection: true));
    private void OnCreateProject(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProjectId is { } id) NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(id, true));
    }
    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfileId is { } id) NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(ManageConnection: true, ProfileId: id));
    }
    private async void OnPromote(object sender, RoutedEventArgs e)
    {
        if (_busy || ViewModel.SelectedProfileId is null) return; _busy = true;
        try { await ViewModel.PromoteAsync(_lifetime.Token); }
        catch (Exception ex) { Result.Text = CloudCommandSecurity.Redact(ex.Message); }
        finally { _busy = false; }
    }
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_busy || ViewModel.SelectedProfileId is null) return;
        var name = ViewModel.Connections.First(c => c.Id == ViewModel.SelectedProfileId).Name;
        if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudConnections_DeleteTitle"), I18n.Format("CloudConnections_DeleteDescription", name), I18n.GetString("Common_Confirm"), XamlRoot)) return;
        _busy = true;
        try { await ViewModel.DeleteAsync(_lifetime.Token); }
        catch (Exception ex) { Result.Text = CloudCommandSecurity.Redact(ex.Message); }
        finally { _busy = false; }
    }
}
