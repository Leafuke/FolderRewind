using FolderRewind.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;

namespace FolderRewind.Controls;

public sealed partial class NotificationCenterControl : UserControl
{
    public NotificationCenterViewModel ViewModel { get; } = new();
    public event EventHandler? CloseRequested;
    private readonly DispatcherQueueTimer _undoTimer;
    private ScrollViewer? _scroll;
    private bool _opened, _pointer, _keyboard, _syncFilter;
    private double _savedOffset;
    private string? _returnId;
    public NotificationCenterControl()
    {
        InitializeComponent();
        _undoTimer = DispatcherQueue.CreateTimer(); _undoTimer.Interval = TimeSpan.FromMilliseconds(250);
        _undoTimer.Tick += (_, _) => ViewModel.RefreshUndo();
        ViewModel.ResetScrollRequested += () => { _savedOffset = 0; _scroll?.ChangeView(null, 0, null, true); FocusHeader(); };
    }
    public void BeginOpen()
    {
        _opened = true; _pointer = _keyboard = false; _savedOffset = 0;
        ViewModel.Open();
        _syncFilter = true; Filter.SelectedItem = ViewModel.ImportantOnly ? ImportantFilter : AllFilter; _syncFilter = false;
        _scroll?.ChangeView(null, 0, null, true); _undoTimer.Start();
    }
    public void EndOpen() { _opened = false; _undoTimer.Stop(); ViewModel.Close(); }
    public void FocusHeader() => (ViewModel.HasSelection ? BackButton : (Control)Filter).Focus(FocusState.Programmatic);
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void EnterDetails(string id, bool navigate)
    {
        if (!ViewModel.HasSelection) { _savedOffset = _scroll?.VerticalOffset ?? 0; _returnId = id; }
        if (navigate) ViewModel.OpenItemTarget(id); else ViewModel.ShowDetails(id);
        DispatcherQueue.TryEnqueue(() => { if (_opened && ViewModel.HasSelection) BackButton.Focus(FocusState.Programmatic); });
    }
    public void ShowDetails(string id) => EnterDetails(id, false);
    private void OnItemClick(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is NotificationItemViewModel item) EnterDetails(item.Id, false); }
    private void OnDetails(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: string id }) EnterDetails(id, false); }
    private void OnTarget(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: string id }) EnterDetails(id, true); }
    private void OnBack(object sender, RoutedEventArgs e)
    {
        ViewModel.SetReadingProtection(true); ViewModel.CloseDetails();
        DispatcherQueue.TryEnqueue(() =>
        {
            Records.UpdateLayout(); _scroll?.ChangeView(null, _savedOffset, null, true);
            var restored = false;
            foreach (var item in ViewModel.Items)
                if (item.Id == _returnId && Records.ContainerFromItem(item) is ListViewItem container)
                { restored = container.Focus(FocusState.Programmatic); break; }
            if (!restored) Filter.Focus(FocusState.Programmatic);
            UpdateProtection();
        });
    }
    private void OnFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    { if (!_opened || _syncFilter) return; ViewModel.ImportantOnly = sender.SelectedItem == ImportantFilter; _scroll?.ChangeView(null, 0, null, true); }
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) return match; if (FindChild<T>(child) is { } nested) return nested; }
        return null;
    }
    private void OnRecordsLoaded(object sender, RoutedEventArgs e)
    { if (_scroll is not null) return; _scroll = FindChild<ScrollViewer>(Records); if (_scroll is not null) _scroll.ViewChanged += (_, _) => UpdateProtection(); }
    private void OnReadingEntered(object sender, PointerRoutedEventArgs e) { _pointer = true; UpdateProtection(); }
    private void OnReadingExited(object sender, PointerRoutedEventArgs e) { _pointer = false; UpdateProtection(); }
    private void OnReadingFocus(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        _keyboard = false;
        for (var node = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, Records)) { _keyboard = true; break; }
        UpdateProtection();
    });
    private void UpdateProtection() { if (_opened) ViewModel.SetReadingProtection(_pointer || _keyboard || (_scroll?.VerticalOffset ?? 0) > 1); }
    private void OnLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        var chrome = Header.ActualHeight + NewResults.ActualHeight + Filter.ActualHeight + UndoRow.ActualHeight + PersistenceStatus.ActualHeight + 56;
        var available = Math.Max(48, MaxHeight - chrome);
        if (double.IsInfinity(available)) available = 380;
        if (Math.Abs(Records.MaxHeight - available) > 1) Records.MaxHeight = DetailsScroll.MaxHeight = available;
    }
}
