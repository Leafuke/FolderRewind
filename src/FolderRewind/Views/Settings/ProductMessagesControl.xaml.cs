using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings;

public sealed partial class ProductMessagesControl : UserControl
{
    public ProductMessagesViewModel ViewModel { get; } = new();
    internal DeferredSettingsSection UpdateSection { get; } = new();
    internal DeferredSettingsSection NoticesSection { get; } = new();
    private bool _active;
    private int _navigationGeneration;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _navigationTimer;
    public ProductMessagesControl() { InitializeComponent(); Unloaded += (_, _) => Deactivate(); }
    internal void Activate() { _active = true; ViewModel.Activate(); Bindings.Update(); }
    internal void Deactivate() { _active = false; _navigationGeneration++; _navigationTimer?.Stop(); ViewModel.Deactivate(); Bindings.StopTracking(); }
    private void OnUpdateExpanded(object? sender, object e)
    {
        if (!_active) return;
        UpdateSection.Create(); Bindings.Update(); ViewModel.ReadUpdate();
    }
    private void OnNoticesExpanded(object? sender, object e)
    {
        if (!_active) return;
        NoticesSection.Create(); Bindings.Update();
    }
    private void OnNoticeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnouncementItemViewModel item) { ViewModel.SelectNotice(item.Id); BringTop(() => NoticeTitle, NoticesExpander); }
    }
    private void BringTop(Func<FrameworkElement?> target, FrameworkElement anchor)
    {
        var generation = ++_navigationGeneration;
        _navigationTimer?.Stop();
        ScrollViewer? scroll = null;
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer { Name: "SettingsScrollViewer" } owner) { scroll = owner; break; }
        if (scroll?.Content is not FrameworkElement content) return;
        scroll.UpdateLayout();
        // SettingsExpander's items are realized against the viewport. First reveal its
        // non-virtualized header; an unrealized child has no usable content coordinates.
        var anchorTop = anchor.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point()).Y;
        scroll.ChangeView(null, Math.Max(0, anchorTop - 8), null, true);
        var timer = _navigationTimer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        var attempts = 0;
        timer.Tick += (_, _) =>
        {
            if (!_active || generation != _navigationGeneration || ++attempts > 20) { timer.Stop(); return; }
            scroll.UpdateLayout();
            // Expansion and the page's reposition transition also adjust scroll anchoring.
            // Let those finish before the final programmatic offset (bounded to one second).
            if (attempts < 8) return;
            if (target() is not { IsLoaded: true, ActualHeight: > 0 } element) return;
            var top = element.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point()).Y;
            if (top < 0) return;
            var desired = Math.Max(0, top - 8);
            scroll.ChangeView(null, desired, null, true);
            LogService.LogInfo($"Product navigation: top={top:F0}; desired={desired:F0}; extent={scroll.ExtentHeight:F0}; viewport={scroll.ViewportHeight:F0}.", nameof(ProductMessagesControl));
            timer.Stop();
        };
        timer.Start();
    }
    internal void ShowTarget(ProductSettingsNavigationParameter target)
    {
        Activate();
        var generation = ++_navigationGeneration;
        TaskObserver.Observe(ShowTargetAsync(target, generation), nameof(ProductMessagesControl));
    }
    private async Task ShowTargetAsync(ProductSettingsNavigationParameter target, int generation)
    {
        await ProductMessageService.InitializeAsync();
        if (!_active || generation != _navigationGeneration) return;
        if (target.Updates)
        {
            UpdateExpander.IsExpanded = true; OnUpdateExpanded(null, EventArgs.Empty);
            UpdateExpander.Focus(FocusState.Programmatic);
            BringTop(() => UpdateBody, UpdateExpander);
        }
        else
        {
            NoticesExpander.IsExpanded = true; OnNoticesExpanded(null, EventArgs.Empty);
            if (target.AnnouncementId is { } id) ViewModel.SelectNotice(id);
            NoticesExpander.Focus(FocusState.Programmatic);
            BringTop(() => target.AnnouncementId is null ? NoticesBody : NoticeTitle, NoticesExpander);
        }
    }
}
