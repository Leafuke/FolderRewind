using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings;

public sealed partial class ProductMessagesControl : UserControl
{
    public ProductMessagesViewModel ViewModel { get; } = new();
    internal DeferredSettingsSection UpdateSection { get; } = new();
    internal DeferredSettingsSection NoticesSection { get; } = new();
    internal DeferredSettingsSection UpdateDetailsSection { get; } = new();
    private bool _active;
    private int _navigationGeneration;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _navigationTimer;

    public ProductMessagesControl()
    {
        InitializeComponent();
        ViewModel.NoticesChanging += OnNoticesChanging;
        Unloaded += (_, _) => Deactivate();
    }
    internal void Activate()
    {
        _active = true;
        ViewModel.Activate();
        Bindings.Update();
    }
    internal void Deactivate()
    {
        _active = false;
        _navigationGeneration++;
        _navigationTimer?.Stop();
        ViewModel.Deactivate();
        Bindings.StopTracking();
    }
    private void OnUpdateDetailsExpanded(object? sender, object e)
    {
        if (!_active) return;
        UpdateDetailsSection.Create();
        Bindings.Update();
    }
    private void OnUpdateExpanded(object? sender, object e)
    {
        if (!_active) return;
        UpdateSection.Create();
        Bindings.Update();
        ViewModel.ReadUpdate();
    }
    private void OnNoticesExpanded(object? sender, object e)
    {
        if (!_active) return;
        NoticesSection.Create();
        Bindings.Update();
    }
    private void OnNoticesChanging(IReadOnlySet<string> visible)
    {
        if (!_active || XamlRoot is null) return;
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: AnnouncementItemViewModel item })
            {
                if (!visible.Contains(item.Id)) DispatcherQueue.TryEnqueue(() =>
                {
                    if (_active && ArchiveToggleButton?.Focus(FocusState.Programmatic) != true)
                        NoticesExpander.Focus(FocusState.Programmatic);
                });
                break;
            }
        }
    }
    private void BringTarget(Func<FrameworkElement?> target, FrameworkElement anchor)
    {
        var generation = ++_navigationGeneration;
        _navigationTimer?.Stop();
        anchor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        var timer = _navigationTimer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        var attempts = 0;
        timer.Tick += (_, _) =>
        {
            if (!_active || generation != _navigationGeneration || ++attempts > 20) { timer.Stop(); return; }
            // The settings page repositions its sections as deferred content is realized.
            if (attempts < 8 || target() is not { IsLoaded: true, ActualHeight: > 0 } element) return;
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            if (FocusManager.FindFirstFocusableElement(element) is Control focus) focus.Focus(FocusState.Programmatic);
            else if (element is Control control) control.Focus(FocusState.Programmatic);
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
            UpdateExpander.IsExpanded = true;
            OnUpdateExpanded(null, EventArgs.Empty);
            BringTarget(() => UpdateBody, UpdateExpander);
        }
        else
        {
            NoticesExpander.IsExpanded = true;
            OnNoticesExpanded(null, EventArgs.Empty);
            if (target.AnnouncementId is { } id && ViewModel.SelectNotice(id))
            {
                BringTarget(() =>
                {
                    var row = ViewModel.Notices.FirstOrDefault(n => n.Id == id);
                    var index = row is null ? -1 : ViewModel.Notices.IndexOf(row);
                    return index < 0 ? null : NoticesRepeater.GetOrCreateElement(index) as FrameworkElement;
                }, NoticesExpander);
            }
            else BringTarget(() => NoticesBody, NoticesExpander);
        }
    }
}
