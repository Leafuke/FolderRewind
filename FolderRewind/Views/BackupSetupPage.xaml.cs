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
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ContentLayout.Width = Math.Min(960, Math.Max(0, e.NewSize.Width - 48));
        var orientation = ContentLayout.Width < 600 ? Orientation.Vertical : Orientation.Horizontal;
        FooterButtons.Orientation = orientation;
        ResultButtons.Orientation = orientation;
    }
    private BackupSetupScenario _entryScenario;
    public BackupSetupPage() { InitializeComponent(); Loaded += OnPageLoaded; }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _active = true;
        ViewModel.Initialize(e.Parameter as BackupSetupNavigationParameter);
        _entryScenario = (e.Parameter as BackupSetupNavigationParameter)?.Scenario ?? BackupSetupScenario.None;
        base.OnNavigatedTo(e);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _active = false; _lifetime.Cancel(); base.OnNavigatedFrom(e); }
    public InfoBarSeverity Severity(SetupMessageSeverity severity) => (InfoBarSeverity)severity;
    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowNot(bool value) => Show(!value);
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { if (_active) ViewModel.ShowError(ex.Message); }
    }
    private async void OnAddFolder(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = await MainWindowService.PickFolderPathAsync(I18n.GetString("Setup_SourcePickerTitle"), "FolderRewind.Setup.Source", MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (_active && !string.IsNullOrWhiteSpace(path)) ViewModel.AddSource(path);
    });
    private void OnRemove(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string path }) ViewModel.RemoveSource(path); }
    private async void OnDestination(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = await MainWindowService.PickFolderPathAsync(I18n.GetString("Setup_DestinationPickerTitle"), "FolderRewind.Setup.Destination", MainWindowService.SuggestedPickerLocation.ComputerFolder);
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
    private async void OnDiscard(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Setup_DiscardDraftTitle"), I18n.GetString("Setup_DiscardDraftBody"), I18n.GetString("Common_Confirm"), XamlRoot)) return;
        BackupSetupSessionStore.Discard();
        ViewModel.RefreshSavedDraft();
        ViewModel.Message = I18n.GetString("Setup_DraftDiscarded");
    });
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
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        var scenario = _entryScenario;
        _entryScenario = BackupSetupScenario.None;
        if (scenario == BackupSetupScenario.Minecraft) OnMinecraft(sender, e);
        else if (scenario == BackupSetupScenario.OtherGame) OnDiscovery(sender, e);
    }
    private void OnScenarioChosen(object? sender, BackupSetupScenario scenario)
    {
        ViewModel.SelectScenario(scenario);
        if (scenario == BackupSetupScenario.Minecraft) OnMinecraft(this, new RoutedEventArgs());
        else if (scenario == BackupSetupScenario.OtherGame) OnDiscovery(this, new RoutedEventArgs());
    }
    private async void OnMinecraft(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        const string pluginId = "com.folderrewind.minerewind";
        if (!FolderRewind.Services.Discovery.GameDiscoveryProviderFactory.GetPluginBatchAvailability(pluginId).IsAvailable)
        {
            if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Setup_MinecraftTitle"), I18n.GetString("Setup_MinecraftConsent"), I18n.GetString("Common_Confirm"), XamlRoot)) return;
            await MinecraftOnboardingService.PrepareBasicPluginAsync();
            if (!_active) return;
        }
        if (!FolderRewind.Services.Discovery.GameDiscoveryProviderFactory.GetPluginBatchAvailability(pluginId).IsAvailable)
            throw new InvalidOperationException(I18n.GetString("Setup_PluginUnavailable"));
        if (_active) NavigationService.NavigateTo("GameDiscovery", GameDiscoveryNavigationParameter.ForPluginDiscovery(pluginId, returnDraftToSetup: true));
    });
}
