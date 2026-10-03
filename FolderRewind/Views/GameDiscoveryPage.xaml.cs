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
using System.Threading;
using Windows.ApplicationModel.DataTransfer;

namespace FolderRewind.Views;

public sealed partial class GameDiscoveryPage : Page
{
    public GameDiscoveryPageViewModel ViewModel { get; } = new();
    private bool _pickingPluginRoot;
    private CancellationTokenSource? _filterCts;
    private bool _narrow;
    private bool _showDetail;
    private Flyout? _detailsFlyout;
    private MenuFlyout? _resourceMenu;

    public GameDiscoveryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync(e.Parameter as GameDiscoveryNavigationParameter);
        if (ViewModel.IsSessionActive) ApplyResponsiveWidth(PageLayout.ActualWidth - 48);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _filterCts?.Cancel();
        RulesSplitView.IsPaneOpen = false;
        _resourceMenu?.Hide();
        _resourceMenu = null;
        _detailsFlyout?.Hide();
        _detailsFlyout = null;
        ViewModel.Dispose();
        base.OnNavigatedFrom(e);
    }

    private async void OnDownloadAndScanClick(object sender, RoutedEventArgs e)
    {
        RulesSplitView.IsPaneOpen = false;
        await ViewModel.DownloadAndScanAsync();
    }
    private async void OnScanClick(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();

    private void OnOpenRulesClick(object sender, RoutedEventArgs e)
    {
        FindName("RulesPane");
        RulesSplitView.IsPaneOpen = true;
    }

    private void OnCloseRulesClick(object sender, RoutedEventArgs e)
    {
        RulesSplitView.IsPaneOpen = false;
        RulesButton.Focus(FocusState.Programmatic);
    }

    private void OnRulesPaneOpened(SplitView sender, object args) => CloseRulesButton?.Focus(FocusState.Programmatic);

    private void OnRulesPaneKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        OnCloseRulesClick(sender, e);
        e.Handled = true;
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private async void OnPluginKnownLocationsClick(object sender, RoutedEventArgs e)
        => await ViewModel.ScanPluginKnownLocationsAsync();

    private async void OnPluginPickRootClick(object sender, RoutedEventArgs e)
    {
        if (_pickingPluginRoot || ViewModel.IsBusy) return;
        _pickingPluginRoot = true;
        try
        {
            var root = await MainWindowService.PickFolderPathAsync(
                I18n.GetString("Setup_InstanceRoot"), "FolderRewind.GameDiscovery.PluginRoot",
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (ViewModel.IsSessionActive && ReferenceEquals(Frame?.Content, this) && !string.IsNullOrWhiteSpace(root))
                await ViewModel.ScanPluginUserRootAsync(root);
        }
        catch (Exception ex)
        {
            if (ViewModel.IsSessionActive && ReferenceEquals(Frame?.Content, this)) await ShowMessageAsync(I18n.GetString("Common_Failed"), ex.Message);
        }
        finally { _pickingPluginRoot = false; }
    }

    private async void OnImportManifestClick(object sender, RoutedEventArgs e)
    {
        var path = await PickYamlAsync("FolderRewind.GameDiscovery.Primary");
        if (ViewModel.IsSessionActive && !string.IsNullOrWhiteSpace(path))
        {
            RulesSplitView.IsPaneOpen = false;
            await ViewModel.ImportAndScanAsync(path);
        }
    }

    private async void OnBrowseSecondaryClick(object sender, RoutedEventArgs e)
    {
        var path = await PickYamlAsync("FolderRewind.GameDiscovery.Secondary");
        if (ViewModel.IsSessionActive && !string.IsNullOrWhiteSpace(path)) ViewModel.SecondaryManifestPath = path;
    }

    private async void OnBrowseOverrideClick(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync(
            I18n.GetString("GameDiscoveryPage_PickOverride"),
            "FolderRewind.GameDiscovery.Override",
            new[] { ".json" });
        if (ViewModel.IsSessionActive && !string.IsNullOrWhiteSpace(path)) ViewModel.OverridePath = path;
    }

    private async void OnAddSteamRootClick(object sender, RoutedEventArgs e) => await AddRootAsync(GameStore.Steam);
    private async void OnAddGogRootClick(object sender, RoutedEventArgs e) => await AddRootAsync(GameStore.Gog);
    private async void OnAddEpicRootClick(object sender, RoutedEventArgs e) => await AddRootAsync(GameStore.Epic);

    private async Task AddRootAsync(GameStore store)
    {
        var path = await MainWindowService.PickFolderPathAsync(
            I18n.Format("GameDiscoveryPage_PickStoreRoot", store),
            $"FolderRewind.GameDiscovery.{store}");
        if (ViewModel.IsSessionActive && !string.IsNullOrWhiteSpace(path)) ViewModel.AddLibraryRoot(store, path);
    }

    private void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameLibraryRootSetting root }) ViewModel.LibraryRoots.Remove(root);
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.SaveSettingsAsync();
        if (!result.Success)
        {
            await ShowMessageAsync(I18n.GetString("GameDiscoveryPage_SaveFailed"), result.ErrorMessage);
        }
    }

    private async void OnFilterChanged(object sender, object e)
    {
        _filterCts?.Cancel();
        var request = new CancellationTokenSource();
        _filterCts = request;
        try
        {
            await Task.Delay(150, request.Token);
            if (ViewModel.IsSessionActive) ApplyFilters();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_filterCts, request)) _filterCts = null;
            request.Dispose();
        }
    }

    private void ApplyFilters()
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
        if (_narrow && GameList.FocusState != FocusState.Unfocused && ViewModel.SelectedGame is not null) _showDetail = true;
        UpdateResponsiveLayout();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveWidth(e.NewSize.Width - 48);
    }

    private void ApplyResponsiveWidth(double width)
    {
        _narrow = width < 900;
        RulesSplitView.OpenPaneLength = Math.Min(420, Math.Max(0, width + 24));
        if (FilterToolbar is not null && SearchBox is not null && StoreFilter is not null && StatusFilter is not null && ToolbarActions is not null)
        {
            var stacked = width < 1000;
            var small = width < 600;
            SearchColumn.Width = new GridLength(1, GridUnitType.Star);
            StoreColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(150);
            StatusColumn.Width = small ? new GridLength(0) : stacked ? GridLength.Auto : new GridLength(190);
            ActionsColumn.Width = stacked ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumnSpan(SearchBox, stacked ? 2 : 1);
            Grid.SetColumn(StoreFilter, stacked ? 0 : 1);
            Grid.SetRow(StoreFilter, stacked ? 1 : 0);
            Grid.SetColumn(StatusFilter, stacked ? 1 : 2);
            Grid.SetRow(StatusFilter, stacked ? 1 : 0);
            Grid.SetColumn(ToolbarActions, small ? 0 : stacked ? 2 : 3);
            Grid.SetColumnSpan(ToolbarActions, small ? 2 : 1);
            Grid.SetRow(ToolbarActions, small ? 2 : 0);
            ToolbarActions.HorizontalAlignment = HorizontalAlignment.Right;
            StoreFilter.Margin = StatusFilter.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
            ToolbarActions.Margin = new Thickness(0, small ? 8 : 0, 0, 0);
        }
        if (StandardFooter is not null && FooterActions is not null && FooterSummary is not null)
        {
            var stackedFooter = width < 600;
            Grid.SetColumnSpan(FooterSummary, stackedFooter ? 2 : 1);
            Grid.SetColumn(FooterActions, stackedFooter ? 0 : 1);
            Grid.SetColumnSpan(FooterActions, stackedFooter ? 2 : 1);
            Grid.SetRow(FooterActions, stackedFooter ? 1 : 0);
            FooterActions.Margin = new Thickness(0, stackedFooter ? 8 : 0, 0, 0);
        }
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (ResultsLayout is null || DetailPane is null || GamesPane is null) return;
        GameColumn.Width = _narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(260);
        DetailColumn.Width = _narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(DetailPane, _narrow ? 0 : 1);
        GamesPane.Visibility = _narrow && _showDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = !_narrow || _showDetail ? Visibility.Visible : Visibility.Collapsed;
        BackToGames.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnBackToGamesClick(object sender, RoutedEventArgs e) { _showDetail = false; UpdateResponsiveLayout(); }
    private void OnBackToResultsClick(object sender, RoutedEventArgs e) => ViewModel.ReturnToResults();
    private void OnGameSelectionClick(object sender, RoutedEventArgs e)
    { if (sender is CheckBox { Tag: GameDiscoveryCandidateItem game }) game.ToggleSelection(); }
    private void OnSetSelectionClick(object sender, RoutedEventArgs e)
    { if (sender is CheckBox { Tag: GameDiscoveryBackupSetItem set }) set.ToggleSelection(); }
    private async void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameDiscoveryResourceItem resource }) return;
        try { var data = new DataPackage(); data.SetText(resource.Expression); Clipboard.SetContent(data); }
        catch (Exception ex) { await ShowMessageAsync(I18n.GetString("Common_Failed"), ex.Message); }
    }
    private async void OnOpenPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GameDiscoveryResourceItem resource }
            && !ShellPathService.TryOpenPath(resource.Candidate.FixedRoot, out var error))
            await ShowMessageAsync(I18n.GetString("Common_Failed"), error ?? string.Empty);
    }
    private void OnResourceActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameDiscoveryResourceItem resource } button) return;
        _resourceMenu?.Hide();
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = I18n.GetString("GameDiscovery_CopyPath.Content"), Tag = resource, Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += OnCopyPathClick;
        var open = new MenuFlyoutItem { Text = I18n.GetString("GameDiscovery_OpenPath.Content"), Tag = resource,
            IsEnabled = resource.CanOpenPath, Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += OnOpenPathClick;
        var details = new MenuFlyoutItem { Text = I18n.GetString("GameDiscovery_Details.Content"), Icon = new SymbolIcon(Symbol.Document) };
        details.Click += (_, _) => ShowResourceDetails(resource, button);
        menu.Items.Add(copy);
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(details);
        // Clearing the menu releases the row references after dismissal or navigation.
        menu.Closed += OnResourceMenuClosed;
        _resourceMenu = menu;
        menu.ShowAt(button);
    }

    private void OnResourceMenuClosed(object? sender, object args)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Closed -= OnResourceMenuClosed;
        menu.Items.Clear();
        if (ReferenceEquals(_resourceMenu, menu)) _resourceMenu = null;
    }

    private void ShowResourceDetails(GameDiscoveryResourceItem resource, FrameworkElement target)
    {
        _detailsFlyout?.Hide();
        var content = new StackPanel { Spacing = 8, MaxWidth = 520 };
        foreach (var text in new[] { resource.Expression, resource.Tags, resource.Evidence })
            if (!string.IsNullOrWhiteSpace(text)) content.Children.Add(new TextBlock
                { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var flyout = new Flyout { Content = content };
        flyout.Closed += OnResourceDetailsClosed;
        _detailsFlyout = flyout;
        flyout.ShowAt(target);
    }

    private void OnResourceDetailsClosed(object? sender, object args)
    {
        if (sender is not Flyout flyout) return;
        flyout.Closed -= OnResourceDetailsClosed;
        flyout.Content = null;
        if (ReferenceEquals(_detailsFlyout, flyout)) _detailsFlyout = null;
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

        if (!ViewModel.IsSessionActive) return;
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
        if (ViewModel.ReturnDraftToSetup)
        {
            try
            {
                var drafts = ViewModel.TakeSelectedSetupDrafts();
                NavigationService.NavigateTo("BackupSetup", new BackupSetupNavigationParameter(Drafts: drafts, DiscoveryReentry: ViewModel.SetupReentry));
            }
            catch (Exception ex) { await ShowMessageAsync(I18n.GetString("Common_Failed"), ex.Message); }
            return;
        }
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
        if (ViewModel.ReturnDraftToSetup)
        {
            try
            {
                var drafts = ViewModel.TakeSelectedSetupDrafts();
                NavigationService.NavigateTo("BackupSetup", new BackupSetupNavigationParameter(Drafts: drafts, DiscoveryReentry: ViewModel.SetupReentry));
            }
            catch (Exception ex) { await ShowMessageAsync(I18n.GetString("Common_Failed"), ex.Message); }
            return;
        }
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
        if (!ViewModel.IsSessionActive) return;
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
            if (!ViewModel.IsSessionActive) return false;
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
        return ViewModel.IsSessionActive;
    }

    private static Task<string?> PickYamlAsync(string settingsIdentifier)
    {
        return MainWindowService.PickFilePathAsync(
            I18n.GetString("GameDiscoveryPage_PickManifest"),
            settingsIdentifier,
            new[] { ".yaml", ".yml" });
    }

    private Task ShowMessageAsync(string title, string content) => ViewModel.IsSessionActive
        ? AppDialogService.Default.ShowMessageAsync(title, content, XamlRoot)
        : Task.CompletedTask;
}
