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
using System;

namespace FolderRewind.Views;

public sealed partial class MergePage : Page
{
    private bool _narrow, _showDetail, _syncScroll;
    private ScrollViewer? _listScroll, _oursScroll, _theirsScroll;
    public MergePageViewModel ViewModel { get; } = new();
    public MergePage()
    {
        InitializeComponent(); DataContext = ViewModel;
        ViewModel.ContentChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.Filter != (FilterPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString())
                foreach (ComboBoxItem item in FilterPicker.Items) if (item.Tag?.ToString() == ViewModel.Filter) { FilterPicker.SelectedItem = item; break; }
            _listScroll?.ChangeView(null, ViewModel.ViewState.ScrollOffset, null, true);
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
        _narrow = e.NewSize.Width < 720;
        ListColumn.Width = _narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(e.NewSize.Width < 1000 ? 220 : ViewModel.ViewState.ListWidth);
        CompareSecondColumn.Width = new GridLength(e.NewSize.Width >= 1000 ? 1 : 0, GridUnitType.Star);
        Grid.SetColumn(TheirsPanel, e.NewSize.Width >= 1000 ? 1 : 0); Grid.SetRow(TheirsPanel, e.NewSize.Width >= 1000 ? 0 : 1);
        UpdateNarrowLayout();
    }
    private void UpdateNarrowLayout()
    {
        FilePanel.Visibility = _narrow && _showDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = _narrow && !_showDetail ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(DetailPanel, _narrow ? 0 : 2); Grid.SetColumnSpan(DetailPanel, _narrow ? 3 : 1);
        Grid.SetColumnSpan(FilePanel, _narrow ? 3 : 1);
        BackToFiles.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        ListSplitter.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
        SplitColumn.Width = new GridLength(_narrow ? 0 : 8);
    }
    private void OnBackToFiles(object sender, RoutedEventArgs e) { _showDetail = false; UpdateNarrowLayout(); ChangeList.Focus(FocusState.Programmatic); }
    private void OnChangeSelected(object sender, SelectionChangedEventArgs e) { if (ChangeList.SelectedItem is not null) { _showDetail = true; UpdateNarrowLayout(); } }
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
    private async void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    { if (SessionPicker.SelectedItem is MergeSessionChoice s) await ViewModel.SelectSessionAsync(s.Id); }
    private async void OnAnalyze(object sender, RoutedEventArgs e) => await ViewModel.AnalyzeAsync();
    private async void OnOurs(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Ours);
    private async void OnTheirs(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Theirs);
    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Operations?.Tracker.Stop();
    private async void OnDownload(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.PrepareReplicasAsync); }
    private async void OnRecompute(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.RecomputeAsync); }
    private async void OnRecover(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.ResumeAsync); }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanImport || ViewModel.SelectedChange is not { } row || ViewModel.Operations is not { } o) return;
        await ViewModel.ExecuteAsync(async () =>
        {
            var path = await MainWindowService.PickFilePathAsync(I18n.GetString("Merge_Manual"), "merge-manual", ["*"]);
            if (path is not null) await o.ImportAsync(row.Conflict, path);
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
