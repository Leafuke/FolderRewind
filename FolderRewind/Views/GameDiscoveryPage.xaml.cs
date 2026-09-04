using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.Services;
using FolderRewind.Services.Discovery;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class GameDiscoveryPage : Page
{
    public GameDiscoveryPageViewModel ViewModel { get; } = new();

    public GameDiscoveryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync(e.Parameter as GameDiscoveryNavigationParameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async void OnDownloadAndScanClick(object sender, RoutedEventArgs e) => await ViewModel.DownloadAndScanAsync();
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) => await ViewModel.DownloadAndScanAsync();
    private async void OnScanClick(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();
    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private async void OnImportManifestClick(object sender, RoutedEventArgs e)
    {
        var path = await PickYamlAsync("FolderRewind.GameDiscovery.Primary");
        if (!string.IsNullOrWhiteSpace(path)) await ViewModel.ImportAndScanAsync(path);
    }

    private async void OnBrowseSecondaryClick(object sender, RoutedEventArgs e)
    {
        var path = await PickYamlAsync("FolderRewind.GameDiscovery.Secondary");
        if (!string.IsNullOrWhiteSpace(path)) ViewModel.SecondaryManifestPath = path;
    }

    private async void OnBrowseOverrideClick(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync(
            I18n.GetString("GameDiscoveryPage_PickOverride"),
            "FolderRewind.GameDiscovery.Override",
            new[] { ".json" });
        if (!string.IsNullOrWhiteSpace(path)) ViewModel.OverridePath = path;
    }

    private async void OnAddSteamRootClick(object sender, RoutedEventArgs e) => await AddRootAsync(GameStore.Steam);
    private async void OnAddGogRootClick(object sender, RoutedEventArgs e) => await AddRootAsync(GameStore.Gog);
    private async void OnAddEpicRootClick(object sender, RoutedEventArgs e) => await AddRootAsync(GameStore.Epic);

    private async Task AddRootAsync(GameStore store)
    {
        var path = await MainWindowService.PickFolderPathAsync(
            I18n.Format("GameDiscoveryPage_PickStoreRoot", store),
            $"FolderRewind.GameDiscovery.{store}");
        if (!string.IsNullOrWhiteSpace(path)) ViewModel.AddLibraryRoot(store, path);
    }

    private void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameLibraryRootSetting root }) ViewModel.LibraryRoots.Remove(root);
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.SaveSettings(out var error))
        {
            await ShowMessageAsync(I18n.GetString("GameDiscoveryPage_SaveFailed"), error);
        }
    }

    private void OnFilterChanged(object sender, object e)
    {
        if (SearchBox == null || StoreFilter == null || StatusFilter == null) return;
        GameStore? store = (StoreFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Steam" => GameStore.Steam,
            "Gog" => GameStore.Gog,
            "Epic" => GameStore.Epic,
            _ => null
        };
        DiscoveryCandidateStatus? status = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "New" => DiscoveryCandidateStatus.New,
            "NewResources" => DiscoveryCandidateStatus.NewResources,
            "UpToDate" => DiscoveryCandidateStatus.UpToDate,
            _ => null
        };
        ViewModel.SetFilters(SearchBox.Text, store, status);
    }

    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedGame = GameList.SelectedItem as GameDiscoveryCandidateItem;
    }

    private async void OnReviewClick(object sender, RoutedEventArgs e)
    {
        var broadRoots = ViewModel.GetSelectedBroadRootResources();
        if (broadRoots.Count > 0)
        {
            if (!await AppDialogService.Default.ConfirmAsync(
                    I18n.GetString("GameDiscovery_BroadRootConfirm_Title"),
                    I18n.Format(
                        "GameDiscovery_BroadRootConfirm_Content",
                        string.Join(Environment.NewLine, broadRoots
                            .Select(resource => $"- {resource.FixedRoot}")
                            .Distinct(StringComparer.OrdinalIgnoreCase))),
                    I18n.GetString("Common_Confirm"),
                    XamlRoot,
                    isDestructive: true))
            {
                return;
            }
        }

        ViewModel.BuildDrafts();
        if (ViewModel.Drafts.Count == 0)
        {
            await ShowMessageAsync(
                I18n.GetString("GameDiscoveryPage_NoSelectionTitle"),
                I18n.GetString("GameDiscoveryPage_NoSelectionContent"));
        }
    }

    private async void OnCommitClick(object sender, RoutedEventArgs e)
    {
        var result = ViewModel.CommitDrafts();
        if (!result.Success)
        {
            await ShowMessageAsync(I18n.GetString("GameDiscoveryPage_CommitFailed"), result.ErrorMessage);
            return;
        }
        if (!await TryInitializeNativeHistoryAsync(result.AddedConfigurationIds)) return;
        await ShowMessageAsync(
            I18n.GetString("GameDiscoveryPage_CommitComplete"),
            I18n.Format("GameDiscoveryPage_CommitSummary", result.AddedConfigurationCount, result.AddedSourceCount));
    }

    private async void OnPluginBatchCommitClick(object sender, RoutedEventArgs e)
    {
        var skippedCount = ViewModel.PluginBatchSkippedCount;
        var result = ViewModel.CommitPluginBatch();
        if (!result.Success)
        {
            await ShowMessageAsync(
                I18n.GetString("GameDiscoveryPage_CommitFailed"),
                result.ErrorMessage);
            return;
        }
        if (!await TryInitializeNativeHistoryAsync(result.AddedConfigurationIds)) return;

        await ShowMessageAsync(
            I18n.GetString("GameDiscovery_PluginBatch_CommitComplete"),
            I18n.Format(
                "GameDiscovery_PluginBatch_CommitSummary",
                result.AddedConfigurationCount,
                skippedCount));
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            _ = NavigationService.NavigateTo("Home");
        }
    }

    private async Task<bool> TryInitializeNativeHistoryAsync(IEnumerable<string> configIds)
    {
        foreach (var configId in configIds)
        {
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(item =>
                string.Equals(item.Id, configId, StringComparison.OrdinalIgnoreCase));
            if (config is null) continue;
            try
            {
                _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(
                    I18n.GetString("History_NativeInitializationFailedTitle"),
                    I18n.Format("History_NativeInitializationFailed", config.Name, ex.Message));
                return false;
            }
        }
        return true;
    }

    private static Task<string?> PickYamlAsync(string settingsIdentifier)
    {
        return MainWindowService.PickFilePathAsync(
            I18n.GetString("GameDiscoveryPage_PickManifest"),
            settingsIdentifier,
            new[] { ".yaml", ".yml" });
    }

    private Task ShowMessageAsync(string title, string content) =>
        AppDialogService.Default.ShowMessageAsync(title, content, XamlRoot);
}
