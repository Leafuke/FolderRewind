using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Windows.System;

namespace FolderRewind.Views;

public sealed partial class SpatialPreviewPage
{
    private ShellPage? _focusShell;
    private readonly HashSet<FlyoutBase> _openFlyouts = [];
    private Action? _nextPopup;
    private long _popupRevision;

    public static Visibility ShowUnless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private static void SetToolbarAction(FrameworkElement button, MenuFlyoutItem menuItem, bool visible)
    {
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        menuItem.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnInformationOpening(object sender, object e)
    {
        if (ViewModel.HasPreviewWarning) MapStatusExpander.IsExpanded = true;
    }

    private void OnShowInformation(object sender, RoutedEventArgs e)
    {
        MapStatusExpander.IsExpanded = true;
        OpenAfterDismiss(() => InformationFlyout.ShowAt(InformationButton));
    }

    private void OnViewFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args) => CommitHeight();

    private void OnViewPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || ModeSelector.IsDropDownOpen) return;
        _heightDragging = false;
        ViewModel.ResetHeightDraft();
        e.Handled = true;
        ViewFlyout.Hide();
    }

    private void OnViewFlyoutClosed(object sender, object e)
    {
        _heightDragging = false;
        // A dismissed editor always reopens at the applied height, including after invalid input.
        ViewModel.ResetHeightDraft();
    }

    private void OnShowCoordinates(object sender, RoutedEventArgs e) => ShowCoordinates();

    private void OnCoordinateOpened(object sender, object e) => CoordinateX.Focus(FocusState.Programmatic);

    private void OnCoordinateShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ShowCoordinates();
        args.Handled = true;
    }

    private void ShowCoordinates()
    {
        if (!_active || !ViewModel.HasLayers) return;
        OpenAfterDismiss(() =>
        {
            if (_active && ViewModel.HasLayers)
                CoordinateFlyout.ShowAt(NavigateButton.Visibility == Visibility.Visible ? NavigateButton : MoreButton);
        });
    }

    private void OnOpenNavigation(object sender, RoutedEventArgs e)
    {
        OpenAfterDismiss(() =>
        {
            if (_active && ViewModel.HasLayers) NavigationFlyout.ShowAt(MoreButton);
        });
    }

    private void TrackFlyouts()
    {
        foreach (var flyout in new FlyoutBase[] { ViewFlyout, NavigationFlyout, CoordinateFlyout, InformationFlyout, MoreFlyout })
        {
            flyout.Opening += (_, _) => _openFlyouts.Add(flyout);
            flyout.Closed += (_, _) => { _openFlyouts.Remove(flyout); ShowNextPopup(); };
        }
    }

    private void OpenAfterDismiss(Action show)
    {
        _popupRevision++;
        _nextPopup = show;
        foreach (var flyout in _openFlyouts.ToArray()) flyout.Hide();
        ShowNextPopup();
    }

    private void ShowNextPopup()
    {
        if (_openFlyouts.Count > 0 || _nextPopup is not { } show) return;
        var revision = _popupRevision;
        _nextPopup = null;
        // Opening only after Closed avoids a dismissing popup stealing its sibling's focus.
        DispatcherQueue.TryEnqueue(() => { if (_active && revision == _popupRevision) show(); });
    }

    private void OnFocusModeChanged(object sender, RoutedEventArgs e)
    {
        if (!FocusModeMenu.IsChecked) { RestoreNavigationPane(); return; }
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ShellPage shell) continue;
            _focusShell = shell;
            shell.SetPreviewFocus(true);
            ViewModel.IsFocusMode = true;
            return;
        }
        ViewModel.IsFocusMode = false;
    }

    private void RestoreNavigationPane()
    {
        _focusShell?.SetPreviewFocus(false);
        _focusShell = null;
        ViewModel.IsFocusMode = false;
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (DetailLayout.IsPaneOpen) OnCloseDetails(sender, e);
        else if (ViewModel.IsFocusMode) RestoreNavigationPane();
        else return;
        e.Handled = true;
    }

    private void OnPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModel.HasLayers) || ViewModel.HasLayers) return;
        ViewFlyout.Hide(); NavigationFlyout.Hide(); CoordinateFlyout.Hide();
        ScaleLabel.Text = ""; ScaleBar.Width = 0;
    }
}
