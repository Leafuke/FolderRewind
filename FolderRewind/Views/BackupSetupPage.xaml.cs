using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace FolderRewind.Views;

public sealed partial class BackupSetupPage : Page
{
    public BackupSetupViewModel ViewModel { get; } = new();
    private bool _active;
    private readonly CancellationTokenSource _lifetime = new();
    public BackupSetupPage() { InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _active = true;
        ViewModel.Initialize(e.Parameter as BackupSetupNavigationParameter);
        base.OnNavigatedTo(e);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _active = false; _lifetime.Cancel(); base.OnNavigatedFrom(e); }
    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowNot(bool value) => Show(!value);
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { if (_active) ViewModel.Message = ex.Message; }
    }
    private async void OnAddFolder(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = await MainWindowService.PickFolderPathAsync(I18n.GetString("Setup_AddFolder.Content"), "FolderRewind.Setup.Source", MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (_active && !string.IsNullOrWhiteSpace(path)) ViewModel.AddSource(path);
    });
    private void OnRemove(object sender, RoutedEventArgs e) { if (SourceList.SelectedItem is string path) ViewModel.RemoveSource(path); }
    private async void OnDestination(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = await MainWindowService.PickFolderPathAsync(I18n.GetString("Setup_Destination.Header"), "FolderRewind.Setup.Destination", MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (_active && !string.IsNullOrWhiteSpace(path)) ViewModel.Destination = path;
    });
    private void OnNext(object sender, RoutedEventArgs e) => ViewModel.Next();
    private void OnBack(object sender, RoutedEventArgs e) => ViewModel.Back();
    private Task<string?> RequestPassword() => new HomeInteractionService(() => XamlRoot).RequestEncryptionPasswordAsync(_lifetime.Token);
    private async void OnCreateBackup(object sender, RoutedEventArgs e) => await RunAsync(() => ViewModel.SubmitAsync(true, RequestPassword, _lifetime.Token));
    private async void OnCreateOnly(object sender, RoutedEventArgs e) => await RunAsync(() => ViewModel.SubmitAsync(false, RequestPassword, _lifetime.Token));
    private async void OnRetry(object sender, RoutedEventArgs e) => await RunAsync(ViewModel.RetryAsync);
    private async void OnLater(object sender, RoutedEventArgs e) => await RunAsync(ViewModel.SaveForLaterAsync);
    private async void OnResume(object sender, RoutedEventArgs e) => await RunAsync(() => { ViewModel.Resume(); return Task.CompletedTask; });
    private void OnDiscard(object sender, RoutedEventArgs e) { try { BackupSetupSessionStore.Discard(); ViewModel.Message = I18n.GetString("Setup_DraftDiscarded"); } catch (Exception ex) { ViewModel.Message = ex.Message; } }
    private void OnCancel(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("Home");
    private void OnProject(object sender, RoutedEventArgs e) { if (ViewModel.CreatedConfigId is { } id) NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(id)); }
    private void OnVersions(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CreatedConfigId is not { } id) return;
        try { ViewModel.RequireCreatedContextCurrent(); }
        catch (Exception ex) { ViewModel.Message = ex.Message; return; }
        var path = ViewModel.Draft.SourceFolders.Count == 1 ? ViewModel.Draft.SourceFolders.Single().Path : ResultSourcePicker.SelectedItem as string;
        if (path is null) { ViewModel.Message = I18n.GetString("Setup_SelectResultSource"); return; }
        NavigationService.NavigateTo("History", ManagerNavigationParameter.ForFolder(id, path));
    }
    private void OnDiscovery(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("GameDiscovery", new GameDiscoveryNavigationParameter { ReturnDraftToSetup = true });
    private async void OnMinecraft(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        const string pluginId = "com.folderrewind.minerewind";
        var option = PluginService.GetAllSupportedConfigKinds().FirstOrDefault(k => k.RequiredPluginId == pluginId);
        if (option is null || !FolderRewind.Services.Discovery.GameDiscoveryProviderFactory.GetPluginBatchAvailability(pluginId).IsAvailable)
        {
            if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Setup_Minecraft.Content"), I18n.GetString("Setup_MinecraftConsent"), I18n.GetString("Common_Confirm"), XamlRoot)) return;
            await MinecraftOnboardingService.PrepareBasicPluginAsync();
            if (!_active) return;
            option = PluginService.GetAllSupportedConfigKinds().FirstOrDefault(k => k.RequiredPluginId == pluginId);
        }
        if (option is null || !FolderRewind.Services.Discovery.GameDiscoveryProviderFactory.GetPluginBatchAvailability(pluginId).IsAvailable)
            throw new InvalidOperationException(I18n.GetString("Setup_PluginUnavailable"));
        var root = await MainWindowService.PickFolderPathAsync(I18n.GetString("Setup_InstanceRoot"), "FolderRewind.Setup.Minecraft", MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (_active && !string.IsNullOrWhiteSpace(root)) NavigationService.NavigateTo("GameDiscovery", GameDiscoveryNavigationParameter.ForPluginBatch(pluginId, option.CreateReference(), root, returnDraftToSetup: true));
    });
}
