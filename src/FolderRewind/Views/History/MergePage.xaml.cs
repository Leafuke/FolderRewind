using FolderRewind.History.Merge;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.System;
using System;
using System.Linq;
using Microsoft.UI.Input;
using Windows.UI.Core;

namespace FolderRewind.Views;

public sealed partial class MergePage : Page
{
    private void OnSpatialMerge(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Navigation is { } navigation && ViewModel.State.Session is { } session)
            NavigationService.NavigateTo("SpatialWorkspace", new SpatialWorkspaceNavigationParameter(navigation.ConfigId, Guid.Parse(navigation.FolderId), SpatialWorkspaceMode.Merge) { SessionId = session.Id });
    }
    private bool _narrow, _showDetail, _syncScroll, _refreshingList, _syncSelection;
    private bool _compareSideBySide = true;
    private string? _announcedStatus;
    private ScrollViewer? _listScroll, _oursScroll, _theirsScroll;
    public MergePageViewModel ViewModel { get; } = new();
    public MergePage()
    {
        InitializeComponent(); DataContext = ViewModel;
        ViewModel.PropertyChanged += (_, _) =>
        {
            UpdateComparisonLayout();
            if (_announcedStatus == ViewModel.Status) return;
            _announcedStatus = ViewModel.Status;
            FrameworkElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
        ViewModel.ContentChanging += () => _refreshingList = true;
        ViewModel.SelectionUpdated += SyncListSelection;
        ViewModel.ContentChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.Filter != (FilterPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString())
                foreach (ComboBoxItem item in FilterPicker.Items) if (item.Tag?.ToString() == ViewModel.Filter) { FilterPicker.SelectedItem = item; break; }
            _listScroll?.ChangeView(null, ViewModel.ViewState.ScrollOffset, null, true);
            _refreshingList = false;
            SyncListSelection();
        });
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MergeNavigationParameter parameter)
            await ViewModel.ExecuteAsync(() => ViewModel.InitializeAsync(parameter));
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Detach(); base.OnNavigatedFrom(e); }
    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else if (ViewModel.Navigation is { } n) NavigationService.NavigateTo("History", n.ReturnContext);
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FilePanel is null) return;
        var width = e.NewSize.Width - PageLayout.Padding.Left - PageLayout.Padding.Right;
        _narrow = width < 720;
        ListColumn.Width = _narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(width < 1000 ? 240 : ViewModel.ViewState.ListWidth);
        TargetPairColumn.Width = new GridLength(_narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(TargetPair, _narrow ? 0 : 1); Grid.SetRow(TargetPair, _narrow ? 1 : 0);
        Grid.SetColumn(FooterActions, width < 1000 ? 0 : 1);
        Grid.SetRow(FooterActions, width < 1000 ? 1 : 0);
        Grid.SetColumnSpan(FooterActions, width < 1000 ? 2 : 1);
        FooterActions.HorizontalAlignment = width < 1000 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        FooterActions.Orientation = width < 520 ? Orientation.Vertical : Orientation.Horizontal;
        FooterGrid.RowSpacing = width < 1000 ? 8 : 0;
        MethodMapColumn.Width = new GridLength(_narrow ? 0 : 1, _narrow ? GridUnitType.Pixel : GridUnitType.Auto);
        Grid.SetColumn(OpenMapButton, _narrow ? 0 : 1); Grid.SetRow(OpenMapButton, _narrow ? 1 : 0);
        OpenMapButton.Margin = new Thickness(0, _narrow ? 8 : 0, 0, 0);
        UpdateNarrowLayout();
    }
    private void OnDetailPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _compareSideBySide = e.NewSize.Width >= 640;
        BatchButtons.Orientation = _compareSideBySide ? Orientation.Horizontal : Orientation.Vertical;
        FileActions.Orientation = _compareSideBySide ? Orientation.Horizontal : Orientation.Vertical;
        UpdateComparisonLayout();
    }
    private void UpdateComparisonLayout()
    {
        if (SingleDetailLayout is null) return;
        // Only real diff content fills the remaining height. Semantic and binary summaries
        // keep their decisions next to the metadata instead of below empty, stretched panes.
        var previewHeight = ViewModel.HasComparisonLines ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SingleDetailLayout.VerticalAlignment = ViewModel.HasComparisonLines ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        ComparisonContentRow.Height = previewHeight;
        CompareFirstRow.Height = previewHeight;
        CompareSecondRow.Height = _compareSideBySide ? new GridLength(0) : previewHeight;
        OursPreviewRow.Height = previewHeight; TheirsPreviewRow.Height = previewHeight;
        CompareSecondColumn.Width = _compareSideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        CompareGrid.RowSpacing = _compareSideBySide ? 0 : 12;
        CompareGrid.ColumnSpacing = _compareSideBySide ? 12 : 0;
        Grid.SetColumn(TheirsPanel, _compareSideBySide ? 1 : 0); Grid.SetRow(TheirsPanel, _compareSideBySide ? 0 : 1);
    }
    private void UpdateNarrowLayout()
    {
        if (ViewModel.SelectionCount == 0) _showDetail = false;
        FilePanel.Visibility = _narrow && _showDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = _narrow && !_showDetail ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(DetailPanel, _narrow ? 0 : 2); Grid.SetColumnSpan(DetailPanel, _narrow ? 3 : 1);
        Grid.SetColumnSpan(FilePanel, _narrow ? 3 : 1);
        BackToFiles.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        ListSplitter.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
        SplitColumn.Width = new GridLength(_narrow ? 0 : 16);
        OpenBatchButton.Visibility = _narrow && ViewModel.ShowBatchActions ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnBackToFiles(object sender, RoutedEventArgs e) { _showDetail = false; UpdateNarrowLayout(); ChangeList.Focus(FocusState.Programmatic); }
    private void OnFilePanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SelectionButtons is not null) SelectionButtons.Orientation = e.NewSize.Width < 260 ? Orientation.Vertical : Orientation.Horizontal;
    }
    private static bool KeyDownNow(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private void OnChangeClicked(object sender, ItemClickEventArgs e)
    {
        if (KeyDownNow(VirtualKey.Control) || KeyDownNow(VirtualKey.Shift)) return;
        if (e.ClickedItem is not MergeChangeItem row) return;
        // ItemClick may precede the platform's selection update. Also replace off-page selections
        // when clicking the sole selected row currently materialized by the ListView.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ViewModel.IsListAvailable || !ViewModel.Changes.Contains(row)) return;
            ViewModel.SelectOnly(row);
            _showDetail = true; UpdateNarrowLayout();
        });
    }
    private async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.A && KeyDownNow(VirtualKey.Control))
        {
            e.Handled = true; await ViewModel.SelectAllAsync();
            if (ViewModel.IsListAvailable && IsLoaded) ChangeList.Focus(FocusState.Keyboard);
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.ClearSelection(); e.Handled = true; return;
        }
        if (_narrow && e.Key == VirtualKey.Enter && ViewModel.SelectionCount > 0)
        {
            OnOpenBatch(sender, e); e.Handled = true; return;
        }
        if (!KeyDownNow(VirtualKey.Control) && e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_refreshingList && ViewModel.IsListAvailable)
                    ViewModel.UpdateSelection(ChangeList.SelectedItems.Cast<MergeChangeItem>().ToArray(), [], [], false);
            });
    }
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncSelection || _refreshingList) return;
        ViewModel.UpdateSelection(ChangeList.SelectedItems.Cast<MergeChangeItem>().ToArray(),
            e.AddedItems.Cast<MergeChangeItem>().ToArray(), e.RemovedItems.Cast<MergeChangeItem>().ToArray(), KeyDownNow(VirtualKey.Control));
    }
    private void SyncListSelection()
    {
        if (_refreshingList || _syncSelection) return;
        _syncSelection = true;
        try
        {
            foreach (var row in ChangeList.SelectedItems.Cast<MergeChangeItem>().ToArray())
                if (!ViewModel.IsSelected(row.Id)) ChangeList.SelectedItems.Remove(row);
            var selectedIds = ChangeList.SelectedItems.Cast<MergeChangeItem>().Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var row in ViewModel.Changes)
                if (ViewModel.IsSelected(row.Id) && !selectedIds.Contains(row.Id)) ChangeList.SelectedItems.Add(row);
        }
        finally { _syncSelection = false; }
        UpdateNarrowLayout();
    }
    private async void OnSelectAll(object sender, RoutedEventArgs e) => await ViewModel.SelectAllAsync();
    private void OnDeselect(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();
    private void OnOpenBatch(object sender, RoutedEventArgs e)
    {
        _showDetail = true; UpdateNarrowLayout(); BackToFiles.Focus(FocusState.Programmatic);
    }
    private void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        ViewModel.ViewState = ViewModel.ViewState with { ListWidth = Math.Clamp(ListColumn.ActualWidth + (e.Key == VirtualKey.Left ? -10 : 10), 200, 420) };
        ListColumn.Width = new(ViewModel.ViewState.ListWidth); e.Handled = true;
    }
    private void OnListResize(object sender, DragDeltaEventArgs e) { ViewModel.ViewState = ViewModel.ViewState with { ListWidth = Math.Clamp(ListColumn.ActualWidth + e.HorizontalChange, 200, 420) }; ListColumn.Width = new(ViewModel.ViewState.ListWidth); }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (FilterPicker.SelectedItem is ComboBoxItem { Tag: string filter }) ViewModel.Filter = filter; }
    private async void OnClear(object sender, RoutedEventArgs e) => await ViewModel.ClearAsync();
    private async void OnUndo(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.UndoAsync); }
    private async void OnNext(object sender, RoutedEventArgs e) { await ViewModel.ExecuteAsync(ViewModel.NextAsync); if (ViewModel.SelectedChange is { } row) ChangeList.ScrollIntoView(row); }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (FindScroll(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }
    private void OnListLoaded(object sender, RoutedEventArgs e)
    {
        var scroll = FindScroll(ChangeList); if (scroll is null || ReferenceEquals(scroll, _listScroll)) return;
        _listScroll = scroll;
        scroll.ViewChanged += async (_, _) =>
        {
            if (_refreshingList) return;
            ViewModel.ViewState = ViewModel.ViewState with { ScrollOffset = scroll.VerticalOffset };
            if (scroll.VerticalOffset >= scroll.ScrollableHeight - 200) await ViewModel.LoadMoreAsync();
        };
    }
    private void OnDiffLoaded(object sender, RoutedEventArgs e)
    {
        var scroll = FindScroll((DependencyObject)sender); if (scroll is null || ReferenceEquals(scroll, _oursScroll) || ReferenceEquals(scroll, _theirsScroll)) return;
        if (ReferenceEquals(sender, OursLinesList)) _oursScroll = scroll; else _theirsScroll = scroll;
        scroll.ViewChanged += (_, _) =>
        {
            if (_syncScroll || CompareSecondColumn.Width.Value == 0) return;
            _syncScroll = true;
            (ReferenceEquals(scroll, _oursScroll) ? _theirsScroll : _oursScroll)?.ChangeView(null, scroll.VerticalOffset, null, true);
            _syncScroll = false;
        };
    }
    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => ViewModel.Notify();
    private void OnNewMerge(object sender, RoutedEventArgs e) { TaskFlyout.Hide(); ViewModel.BeginNewMerge(); SourcePicker.Focus(FocusState.Programmatic); }
    private void OnCancelNew(object sender, RoutedEventArgs e) => ViewModel.CancelNewMerge();
    private async void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    { if (SessionPicker.SelectedItem is MergeSessionChoice s) await ViewModel.SelectSessionAsync(s.Id); }
    private async void OnAnalyze(object sender, RoutedEventArgs e) => await ViewModel.AnalyzeAsync();
    private async void OnGenerate(object sender, RoutedEventArgs e) => await ViewModel.GenerateAsync();
    private async void OnApplyReview(object sender, RoutedEventArgs e) => await ViewModel.ApplyReviewAsync();
    private void OnEditDecisions(object sender, RoutedEventArgs e) => ViewModel.EditDecisions();
    private async void OnViewResult(object sender, RoutedEventArgs e) => await ViewModel.ExecuteAsync(() => ViewModel.ShowResultAsync(false));
    private async void OnViewProtection(object sender, RoutedEventArgs e) => await ViewModel.ExecuteAsync(() => ViewModel.ShowResultAsync(true));
    private async void OnOurs(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Ours);
    private async void OnTheirs(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Theirs);
    private async void OnAdoptBase(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Base);
    private async void OnDeleteChoice(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Delete);
    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Operations?.Tracker.Stop();
    private async void OnDownload(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.PrepareReplicasAsync); }
    private async void OnRecompute(object sender, RoutedEventArgs e) { ViewModel.ClearSelection(); if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.RecomputeAsync); }
    private async void OnRecover(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.ResumeAsync); }
    private async void OnReload(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) { await ViewModel.ExecuteAsync(() => o.LoadAsync()); await ViewModel.ReloadChangesAsync(); } }
    private async void OnRetrySave(object sender, RoutedEventArgs e) => await ViewModel.RetryDecisionSaveAsync();
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanImport || ViewModel.SelectedChange is not { } row || ViewModel.Operations is not { } o) return;
        var original = o.Snapshot.Session;
        await ViewModel.ExecuteAsync(async () =>
        {
            var path = await MainWindowService.PickFilePathAsync("merge-manual", ["*"]);
            if (path is not null)
            {
                if (original?.Id != o.Snapshot.Session?.Id || original?.Revision != o.Snapshot.Session?.Revision)
                    throw new InvalidOperationException(I18n.GetString("Merge_Diagnostic_Stale"));
                await o.ImportAsync(row.Conflict, path);
            }
        });
    }
    private async void OnAbandon(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Operations is not { } o || !ViewModel.CanAbandon) return;
        var dialog = new ContentDialog { Title = I18n.GetString("Merge_Abandon"), Content = I18n.GetString("MergeWorkspace_AbandonConfirm"),
            PrimaryButtonText = I18n.GetString("Merge_Abandon"), CloseButtonText = I18n.GetString("Merge_Close"), DefaultButton = ContentDialogButton.Close };
        await ViewModel.ExecuteAsync(async () =>
        {
            if (await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot) == ContentDialogResult.Primary) await o.AbandonAsync();
        });
    }
}
