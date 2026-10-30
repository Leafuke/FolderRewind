using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;

namespace FolderRewind.Views;

public sealed partial class ShellPage
{
    private bool _notificationOpen, _restoreNotificationFocus, _navigationClosing;
    private string? _pendingDetailsId;
    private void InitializeNotificationCenter()
    {
        NotificationToast.SetShadowReceiver(NavView);
        NotificationCenter.ViewModel.Activate();
        NotificationCenter.ViewModel.Navigated += OnNotificationNavigated;
        NotificationNavigationService.DetailsRequested += OnNotificationDetailsRequested;
    }
    private void DisposeNotificationCenter()
    {
        NotificationService.SetCenterOpen(false); NotificationCenter.EndOpen();
        NotificationNavigationService.DetailsRequested -= OnNotificationDetailsRequested;
        NotificationCenter.ViewModel.Navigated -= OnNotificationNavigated;
        NotificationCenter.ViewModel.Dispose();
    }
    public void SetTitleBarInsets(double left, double right)
    { AppTitleBar.Padding = new Thickness(12 + left, 0, 12, 0); CaptionButtonInset.Width = Math.Max(0, right); }
    private void OnNotificationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (!ReferenceEquals(e.InvokedItemContainer, NotificationBell)) return;
        if (_notificationOpen) { _restoreNotificationFocus = true; NotificationFlyout.Hide(); }
        else OpenNotificationCenter();
    }
    private void OpenNotificationCenter()
    {
        if (_notificationOpen) return;
        _restoreNotificationFocus = false; _notificationOpen = true;
        NotificationCenter.BeginOpen(); NotificationService.SetCenterOpen(true);
        NotificationFlyout.ShowAt(NotificationBell);
    }
    private void OnNotificationFlyoutOpened(object sender, object e)
        => DispatcherQueue.TryEnqueue(NotificationCenter.FocusHeader);
    private void OnNotificationDetailsRequested(string id)
    {
        if (_navigationClosing) { _pendingDetailsId = id; return; }
        OpenNotificationCenter(); NotificationCenter.ShowDetails(id);
    }
    private void OnNotificationCloseRequested(object? sender, EventArgs e)
    { _restoreNotificationFocus = true; NotificationFlyout.Hide(); }
    private void OnNotificationNavigated()
    { _restoreNotificationFocus = false; _navigationClosing = true; NotificationFlyout.Hide(); }
    private void OnNotificationFlyoutClosed(object sender, object e)
    {
        _notificationOpen = false; NotificationCenter.EndOpen(); NotificationService.SetCenterOpen(false);
        if (_restoreNotificationFocus) NotificationBell.Focus(FocusState.Programmatic);
        var navigated = _navigationClosing; _navigationClosing = false;
        if (_pendingDetailsId is { } id)
        {
            _pendingDetailsId = null;
            DispatcherQueue.TryEnqueue(() => OnNotificationDetailsRequested(id));
        }
        else if (navigated) DispatcherQueue.TryEnqueue(() =>
        {
            if (_notificationOpen) return;
            for (var node = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, ContentFrame)) return;
            if (FocusManager.FindFirstFocusableElement(ContentFrame) is Control control) control.Focus(FocusState.Programmatic);
        });
        _restoreNotificationFocus = false;
    }
    private void OnNotificationAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NotificationCenter is null || NotificationToast is null) return;
        // Content must fit inside the native presenter stroke, not exceed its outer bounds.
        var border = Application.Current.Resources.TryGetValue("FlyoutBorderThemeThickness", out var value) && value is Thickness thickness
            ? thickness : new Thickness(1);
        NotificationCenter.Width = Math.Max(0, Math.Min(420, e.NewSize.Width - 24) - border.Left - border.Right);
        NotificationCenter.MaxHeight = Math.Max(0, Math.Min(560, e.NewSize.Height - 44 - 24) - border.Top - border.Bottom);
        NotificationToast.Width = Math.Min(380, Math.Max(0, e.NewSize.Width - 32));
        NotificationToast.MaxHeight = Math.Max(0, e.NewSize.Height - 44 - 32);
    }
    private void OnNotificationKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !_notificationOpen) return;
        _restoreNotificationFocus = true; NotificationFlyout.Hide(); e.Handled = true;
    }
}
