using FolderRewind.Models;
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
        await ViewModel.InitializeAsync();
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
        if (!string.IsNullOrWhiteSpace(path)) ViewModel.Settings.SecondaryManifestPath = path;
    }

    private async void OnBrowseOverrideClick(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync(
            I18n.GetString("GameDiscoveryPage_PickOverride"),
            "FolderRewind.GameDiscovery.Override",
            new[] { ".json" });
        if (!string.IsNullOrWhiteSpace(path)) ViewModel.Settings.OverridePath = path;
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
        if (sender is Button { Tag: GameLibraryRootSetting root }) ViewModel.Settings.LibraryRoots.Remove(root);
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
        await ShowMessageAsync(
            I18n.GetString("GameDiscoveryPage_CommitComplete"),
            I18n.Format("GameDiscoveryPage_CommitSummary", result.AddedConfigurationCount, result.AddedSourceCount));
    }

    private static Task<string?> PickYamlAsync(string settingsIdentifier)
    {
        return MainWindowService.PickFilePathAsync(
            I18n.GetString("GameDiscoveryPage_PickManifest"),
            settingsIdentifier,
            new[] { ".yaml", ".yml" });
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = I18n.GetString("Common_Ok"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
