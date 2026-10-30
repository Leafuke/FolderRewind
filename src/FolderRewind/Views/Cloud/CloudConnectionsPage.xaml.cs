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
    private void OnCreateSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var required = CreateSharedButton.DesiredSize.Width + 280 + CreateProjectButton.DesiredSize.Width + 24;
        var narrow = e.NewSize.Width < Math.Max(760, required);
        CreateActions.RowSpacing = narrow ? 12 : 0;
        CreateActions.ColumnDefinitions[1].Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        CreateActions.ColumnDefinitions[2].Width = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(ConnectionProjectPicker, narrow ? 1 : 0);
        Grid.SetColumn(ConnectionProjectPicker, narrow ? 0 : 1);
        Grid.SetColumnSpan(ConnectionProjectPicker, narrow ? 2 : 1);
        ConnectionProjectPicker.Width = Math.Min(280, Math.Max(0, e.NewSize.Width - CreateProjectButton.DesiredSize.Width - 12));
        Grid.SetRow(CreateProjectButton, narrow ? 1 : 0);
    }
    private void OnConnectionActionsSizeChanged(object sender, SizeChangedEventArgs e)
    { if (sender is StackPanel panel) panel.Orientation = e.NewSize.Width < 640 ? Orientation.Vertical : Orientation.Horizontal; }
    private void OnSelected(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedProfileId = ((sender as ListView)?.SelectedItem as CloudConnectionDisplay)?.Id;
    private void OnConnectionContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not CloudConnectionDisplay connection) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, connection.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, "CloudConnection_" + connection.Id);
    }
    private void OnCreateShared(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(ManageConnection: true));
    private void OnCreateProject(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProjectId is { } id) NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(id, true));
    }
    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfileId is { } id) NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(ManageConnection: true, ProfileId: id));
    }
    private void OnReauthenticate(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfileId is { } id) NavigationService.NavigateTo("CloudSetup",
            new CloudSetupNavigationParameter(ManageConnection: true, ProfileId: id, Reauthenticate: true));
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
