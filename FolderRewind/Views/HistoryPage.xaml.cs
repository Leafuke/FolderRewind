using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class HistoryPage : Page
{
    private async void OnLoadMoreClick(object sender, RoutedEventArgs e) => await ViewModel.LoadMoreAsync();

    private bool _isNavigating;
    private HistoryReturnContext? _mergeReturnContext;

    public HistoryPageViewModel ViewModel { get; }

    public HistoryPage()
    {
        ViewModel = new HistoryPageViewModel(new HistoryInteractionService(() => XamlRoot));
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ViewModel.Initialize();
        Loaded += (_, _) => HistoryViewSelector.SelectedItem = ViewModel.IsGroupedRunView ? RunHistoryViewItem : SourceHistoryViewItem;
        Loaded += OnLegacyMigrationLoaded;

        // 首次导航时显式设置集合，避免早期 WinUI 版本在缓存页面上延后建立绑定。
        ConfigFilter.ItemsSource = ViewModel.Configs;
        HistoryList.ItemsSource = ViewModel.FilteredHistory;
        RunHistoryList.ItemsSource = ViewModel.FilteredRuns;
        BranchFilter.ItemsSource = ViewModel.Branches;
        UseColorsToggleMenuItem.IsChecked = ViewModel.UseHistoryStatusColors;
        PresentationSelector.SelectedItem = ViewModel.IsAdvancedHistory ? AdvancedHistoryItem : NormalHistoryItem;
        HistoryViewSelector.SelectedItem = ViewModel.IsGroupedRunView
            ? RunHistoryViewItem
            : SourceHistoryViewItem;
    }

    private async void OnPresentationSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        try { await ViewModel.SetPresentationModeAsync(sender.SelectedItem == AdvancedHistoryItem ? HistoryPresentationMode.Advanced : HistoryPresentationMode.Normal); }
        catch (Exception ex) { ViewModel.ReportLoadFailure(ex.Message); }
    }

    private void OnOpenAdvancedClick(object sender, RoutedEventArgs e) => PresentationSelector.SelectedItem = AdvancedHistoryItem;

    private void OnHistoryPageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFilterLayout(e.NewSize.Width);

    private void UpdateFilterLayout(double width)
    {
        if (FiltersGrid is null) return;
        var narrow = width < 860;
        Grid.SetColumn(CommentFilterBox, narrow ? 0 : 2);
        Grid.SetRow(CommentFilterBox, narrow ? 1 : 0);
        Grid.SetColumn(GroupingPanel, narrow ? 1 : 3);
        Grid.SetRow(GroupingPanel, narrow ? 1 : 0);
        FiltersGrid.ColumnDefinitions[1].Width = ViewModel.IsGroupedRunView && !narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FiltersGrid.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        FiltersGrid.ColumnDefinitions[3].Width = new GridLength(0, GridUnitType.Auto);
        Grid.SetColumn(PresentationSelector, narrow ? 0 : 1);
        Grid.SetRow(PresentationSelector, narrow ? 1 : 0);
        Grid.SetColumnSpan(PresentationSelector, narrow ? 2 : 1);
        Grid.SetColumnSpan(HistoryPageTitle, narrow ? 2 : 1);
    }

    private void OnBranchToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BranchActionsPanel is null || BranchStatus is null) return;
        var narrow = e.NewSize.Width < 880;
        BranchSelectionGrid.RowSpacing = narrow ? 8 : 0;
        Grid.SetColumn(BranchActionsPanel, narrow ? 0 : 2);
        Grid.SetRow(BranchActionsPanel, narrow ? 1 : 0);
        Grid.SetColumnSpan(BranchActionsPanel, narrow ? 4 : 1);
        Grid.SetColumn(BranchStatus, narrow ? 0 : 3);
        Grid.SetRow(BranchStatus, narrow ? 2 : 0);
        Grid.SetColumnSpan(BranchStatus, narrow ? 4 : 1);
        BranchSelectionGrid.ColumnDefinitions[1].Width = narrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(200);
        BranchSelectionGrid.ColumnDefinitions[3].Width = narrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
    }

    private void OnHistoryCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.Children.Count < 2) return;
        var narrow = e.NewSize.Width < 520;
        if (grid.Children[1] is not FrameworkElement actions) return;
        if (actions is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement { Tag: "HistoryCompactAction" } compact)
                    compact.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                if (child is DropDownButton { Flyout: MenuFlyout menu })
                    foreach (var entry in menu.Items)
                        if (entry.Name.StartsWith("Compact", StringComparison.Ordinal))
                            entry.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        SetHistorySizeVisibility(grid.Children[0], narrow);
    }

    private static void SetHistorySizeVisibility(DependencyObject parent, bool narrow)
    {
        if (parent is FrameworkElement { Tag: "HistorySize" } size)
            size.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            SetHistorySizeVisibility(VisualTreeHelper.GetChild(parent, i), narrow);
    }

    private void OnHistoryContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ClearContainerAutomationMetadata(args);
            return;
        }

        if (args.Item is NativeHistoryVersionViewItem item)
        {
            AutomationProperties.SetName(args.ItemContainer, item.Message);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HistoryVersionItem_{item.AutomationIdentity}");
        }
    }

    private void OnRunContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ClearContainerAutomationMetadata(args);
            return;
        }

        if (args.Item is BackupRunViewItem item)
        {
            AutomationProperties.SetName(args.ItemContainer, item.Message);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HistoryRunItem_{item.RunId}");
        }
    }

    private static void ClearContainerAutomationMetadata(ContainerContentChangingEventArgs args)
    {
        AutomationProperties.SetName(args.ItemContainer, string.Empty);
        AutomationProperties.SetAutomationId(args.ItemContainer, string.Empty);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _legacyNoticeActive = true;
        base.OnNavigatedTo(e);

        var returnContext = e.Parameter as HistoryReturnContext ?? (e.NavigationMode == NavigationMode.Back ? _mergeReturnContext : null);
        if (returnContext is not null)
        {
            var restoredFolder = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == returnContext.ConfigId)?.SourceFolders.FirstOrDefault(f => f.Id == returnContext.FolderId);
            if (restoredFolder is not null)
            {
                await ApplySelectionFromNavigationAsync(returnContext.ConfigId, restoredFolder.Path);
                await ViewModel.SetPresentationModeAsync(returnContext.Presentation);
                ViewModel.CommentFilterText = returnContext.Search;
                ViewModel.SelectedBranch = ViewModel.Branches.FirstOrDefault(b => b.BranchId == returnContext.SelectedBranch);
                DispatcherQueue.TryEnqueue(() => { HistoryList.UpdateLayout(); FindHistoryScroll(HistoryList)?.ChangeView(null, returnContext.ScrollOffset, null, true); });
                if (returnContext.FocusCheckpoint is { } checkpoint && ViewModel.FilteredHistory.FirstOrDefault(item => item.CheckpointId == checkpoint) is { } focus)
                    DispatcherQueue.TryEnqueue(() => { HistoryList.SelectedItem = focus; HistoryList.ScrollIntoView(focus); HistoryList.Focus(FocusState.Programmatic); });
                if (returnContext.OpenSafetySnapshots) await ViewModel.ManageSafetySnapshotsCommand.ExecuteAsync(null);
                return;
            }
        }

        if (e.Parameter is ManagerNavigationParameter managerParameter)
        {
            await ApplySelectionFromNavigationAsync(managerParameter.ConfigId, managerParameter.FolderPath);
            return;
        }

        if (e.Parameter is ManagedFolder folder)
        {
            await ApplySelectionFromNavigationAsync(null, folder.Path);
            return;
        }

        await RestoreLastSelectionAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (e.SourcePageType == typeof(MergePage))
            _mergeReturnContext = ViewModel.CaptureReturnContext(FindHistoryScroll(HistoryList)?.VerticalOffset ?? 0);
        _legacyNoticeActive = false;
        ResetLegacyMigrationNotice();
        ViewModel.Suspend();
        base.OnNavigatedFrom(e);
    }

    private void OnPendingMergeClick(object sender, RoutedEventArgs e) => ViewModel.OpenMergeWorkspace();
    private static ScrollViewer? FindHistoryScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindHistoryScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private async Task ApplySelectionFromNavigationAsync(string? configId, string? folderPath)
    {
        _isNavigating = true;
        BackupConfig? config;
        ManagedFolder? folder;
        try
        {
            if (!ViewModel.TryResolveSelection(configId, folderPath, out config, out folder)
                || config is null)
            {
                return;
            }

            ConfigFilter.SelectedItem = config;
            ConfigureFolderFilter(config, folder);
        }
        finally
        {
            _isNavigating = false;
        }

        await SelectHistoryAsync(config, folder, folder is not null, true);
    }

    private async Task RestoreLastSelectionAsync()
    {
        if (_isNavigating
            || !ViewModel.TryResolveLastSelection(out var config, out var folder)
            || config is null)
        {
            return;
        }

        _isNavigating = true;
        try
        {
            ConfigFilter.SelectedItem = config;
            ConfigureFolderFilter(config, folder);
        }
        finally
        {
            _isNavigating = false;
        }

        await SelectHistoryAsync(config, folder, folder is not null, true);
    }

    private async void ConfigFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isNavigating || ConfigFilter.SelectedItem is not BackupConfig config)
        {
            return;
        }

        ManagedFolder? folder;
        _isNavigating = true;
        try
        {
            ConfigureFolderFilter(config, null);
            folder = FolderFilter.SelectedItem as ManagedFolder;
        }
        finally
        {
            _isNavigating = false;
        }

        await SelectHistoryAsync(config, folder, folder is not null, true);
    }

    private async void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isNavigating
            || ViewModel.IsGroupedRunView
            || FolderFilter.SelectedItem is not ManagedFolder folder
            || ConfigFilter.SelectedItem is not BackupConfig config)
        {
            return;
        }

        await SelectHistoryAsync(config, folder, true, true);
    }

    private async void OnHistoryViewSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (_isNavigating || sender.SelectedItem?.Tag is not string tag)
        {
            return;
        }

        var mode = string.Equals(tag, "Run", StringComparison.OrdinalIgnoreCase)
            ? HistoryViewMode.ByRun
            : HistoryViewMode.PerSource;
        await ViewModel.ChangeViewModeCommand.ExecuteAsync(mode);

        if (ConfigFilter.SelectedItem is BackupConfig config)
        {
            ViewModel.TryGetCurrentSelection(out _, out var currentFolder);
            ConfigureFolderFilter(config, currentFolder);
        }
    }

    private async Task SelectHistoryAsync(
        BackupConfig config,
        ManagedFolder? folder,
        bool refreshHistory,
        bool persistSelection)
    {
        ResetLegacyMigrationNotice();
        try
        {
            await ViewModel.ChangeSelectionCommand.ExecuteAsync(
                new HistoryPageViewModel.HistorySelectionRequest(config, folder, refreshHistory, persistSelection));
        }
        finally
        {
            if (ViewModel.TryGetCurrentConfig(out var current) && current?.Id == config.Id)
                await RefreshLegacyMigrationNoticeAsync();
        }
    }

    private void ConfigureFolderFilter(BackupConfig config, ManagedFolder? preferredFolder)
    {
        var grouped = ViewModel.IsGroupedRunView;
        UpdateFilterLayout(ActualWidth);
        FolderFilter.IsEnabled = !grouped;
        FolderFilter.PlaceholderText = grouped
            ? I18n.GetString("History_Run_AllSources")
            : string.Empty;
        FolderFilter.ItemsSource = grouped ? null : config.SourceFolders;
        FolderFilter.SelectedItem = grouped ? null : preferredFolder;
        if (!grouped && preferredFolder is null)
        {
            FolderFilter.SelectedIndex = config.SourceFolders.Count > 0 ? 0 : -1;
        }
        ScanRecoverMenuItem.IsEnabled = !grouped;
    }

    private void OnViewClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.ViewVersionCommand);

    private void OnEditCommentClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.EditVersionCommentCommand);

    private void OnToggleImportantClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.ToggleVersionImportantCommand);

    private void OnCreateBranchFromVersionClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.CreateBranchFromVersionCommand);

    private void OnUploadToCloudClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.UploadVersionCommand);

    private void OnDownloadFromCloudClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.DownloadVersionCommand);

    private void OnRestoreClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.RestoreVersionCommand);
    private void OnExportClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.ExportVersionCommand);

    private void OnDeleteClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.DeleteVersionCommand);

    private void OnEditRunCommentClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.EditRunCommentCommand);

    private void OnToggleRunImportantClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.ToggleRunImportantCommand);

    private void OnCreateBranchFromRunSourceClick(object sender, RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunSourceViewItem>(sender, ViewModel.CreateBranchFromRunSourceCommand);

    private void OnRestoreRunSourceClick(object sender, RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunSourceViewItem>(sender, ViewModel.RestoreRunSourceCommand);

    private async void OnShowRunSourceHistoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupRunSourceViewItem item) return;
        await ViewModel.ShowRunSourceHistoryCommand.ExecuteAsync(item);
        ViewModel.TryGetCurrentSelection(out var config, out var folder);
        if (config is null) return;
        _isNavigating = true;
        try
        {
            HistoryViewSelector.SelectedItem = SourceHistoryViewItem;
            ConfigureFolderFilter(config, folder);
        }
        finally { _isNavigating = false; }
    }

    private void OnRestoreRunClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.RestoreRunCommand);

    private void OnDeleteRunClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.DeleteRunCommand);

    private static void ExecuteItemCommand<T>(object sender, System.Windows.Input.ICommand command)
        where T : class
    {
        if (sender is FrameworkElement control
            && (control.Tag as T ?? control.DataContext as T) is { } item
            && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }

    private void CommentFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ViewModel.CommentFilterText = textBox.Text;
        }
    }
}
