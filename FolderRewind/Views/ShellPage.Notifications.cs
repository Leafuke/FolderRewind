using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;

namespace FolderRewind.Views;

public sealed partial class ShellPage
{
    private bool _restoreNotificationFocus = true;

    private void InitializeNotificationCenter()
    {
        NotificationCenter.ViewModel.Activate();
        NotificationCenter.ViewModel.Navigated += OnNotificationNavigated;
        NotificationNavigationService.DetailsRequested += OnNotificationDetailsRequested;
    }

    private void DisposeNotificationCenter()
    {
        NotificationNavigationService.DetailsRequested -= OnNotificationDetailsRequested;
        NotificationCenter.ViewModel.Navigated -= OnNotificationNavigated;
        NotificationCenter.ViewModel.Dispose();
    }

    public void SetTitleBarInsets(double left, double right)
    {
        AppTitleBar.Padding = new Thickness(12 + left, 0, 12, 0);
        CaptionButtonInset.Width = Math.Max(0, right);
    }

    private void OnNotificationBellClick(object sender, RoutedEventArgs e)
    {
        _restoreNotificationFocus = true;
        NotificationSplitView.IsPaneOpen = !NotificationSplitView.IsPaneOpen;
        if (NotificationSplitView.IsPaneOpen) DispatcherQueue.TryEnqueue(NotificationCenter.FocusHeader);
    }

    private void OnNotificationDetailsRequested(string id)
    {
        NotificationCenter.ViewModel.ShowDetails(id);
        _restoreNotificationFocus = true;
        NotificationSplitView.IsPaneOpen = true;
        DispatcherQueue.TryEnqueue(NotificationCenter.FocusHeader);
    }

    private void OnNotificationCloseRequested(object? sender, EventArgs e) => NotificationSplitView.IsPaneOpen = false;
    private void OnNotificationNavigated()
    {
        _restoreNotificationFocus = false;
        NotificationSplitView.IsPaneOpen = false;
    }
    private void OnNotificationPaneClosed(SplitView sender, object args)
    {
        if (_restoreNotificationFocus) NotificationBell.Focus(FocusState.Programmatic);
        _restoreNotificationFocus = true;
    }
    private void OnNotificationAreaSizeChanged(object sender, SizeChangedEventArgs e)
        => NotificationSplitView.OpenPaneLength = Math.Min(420, Math.Max(0, e.NewSize.Width));
    private void OnNotificationKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !NotificationSplitView.IsPaneOpen) return;
        NotificationSplitView.IsPaneOpen = false;
        e.Handled = true;
    }
}
