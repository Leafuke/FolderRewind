using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class HistoryPage : Page
{
    private bool _isNavigating;

    public HistoryPageViewModel ViewModel { get; }

    public HistoryPage()
    {
        ViewModel = new HistoryPageViewModel(new HistoryInteractionService(() => XamlRoot));
        InitializeComponent();
        ViewModel.Initialize();

        // 首次导航时显式设置集合，避免早期 WinUI 版本在缓存页面上延后建立绑定。
        ConfigFilter.ItemsSource = ViewModel.Configs;
        HistoryList.ItemsSource = ViewModel.FilteredHistory;
        RunHistoryList.ItemsSource = ViewModel.FilteredRuns;
        BranchFilter.ItemsSource = ViewModel.Branches;
        UseColorsToggleMenuItem.IsChecked = ViewModel.UseHistoryStatusColors;
        HistoryViewSelector.SelectedItem = ViewModel.IsGroupedRunView
            ? RunHistoryViewItem
            : SourceHistoryViewItem;
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
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HistoryVersionItem_{item.VersionId}");
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
        base.OnNavigatedTo(e);

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
        ViewModel.Suspend();
        base.OnNavigatedFrom(e);
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
        if (sender.SelectedItem?.Tag is not string tag)
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

    private Task SelectHistoryAsync(
        BackupConfig config,
        ManagedFolder? folder,
        bool refreshHistory,
        bool persistSelection)
        => ViewModel.ChangeSelectionCommand.ExecuteAsync(
            new HistoryPageViewModel.HistorySelectionRequest(
                config,
                folder,
                refreshHistory,
                persistSelection));

    private void ConfigureFolderFilter(BackupConfig config, ManagedFolder? preferredFolder)
    {
        var grouped = ViewModel.IsGroupedRunView;
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

    private void OnDeleteClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<NativeHistoryVersionViewItem>(sender, ViewModel.DeleteVersionCommand);

    private void OnEditRunCommentClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.EditRunCommentCommand);

    private void OnToggleRunImportantClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.ToggleRunImportantCommand);

    private void OnCreateBranchFromRunClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.CreateBranchFromRunCommand);

    private void OnRestoreRunClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.RestoreRunCommand);

    private void OnDeleteRunClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ExecuteItemCommand<BackupRunViewItem>(sender, ViewModel.DeleteRunCommand);

    private static void ExecuteItemCommand<T>(object sender, System.Windows.Input.ICommand command)
        where T : class
    {
        if (sender is Button { DataContext: T item } && command.CanExecute(item))
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
