using FolderRewind.Controls;
using FolderRewind.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;

namespace FolderRewind.Services;

/// <summary>Two independent surfaces share the tested scheduler, not their queues or focus.</summary>
internal sealed class ShellNotificationPresenter : IDisposable
{
    private readonly InfoBar _bar;
    private readonly NotificationToastControl _toast;
    private readonly InfoBadge _badge;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly NotificationScheduler _temporary = new(TimeProvider.System), _persistent = new(TimeProvider.System);
    private bool _disposed, _rendering, _toastHover, _toastFocus, _barHover, _barFocus;
    private int _toastRevision = -1, _barRevision = -1;
    private string? _toastGroup, _barGroup;
    public ShellNotificationPresenter(InfoBar bar, NotificationToastControl toast, InfoBadge badge, DispatcherQueue dispatcher)
    {
        _bar = bar; _toast = toast; _badge = badge; _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250); _timer.IsRepeating = true; _timer.Tick += OnTick;
        _bar.GotFocus += OnFocusChanged; _bar.LostFocus += OnFocusChanged;
        _toast.GotFocus += OnFocusChanged; _toast.LostFocus += OnFocusChanged;
        _toast.PointerEntered += OnToastEntered; _toast.PointerExited += OnToastExited;
        _toast.CloseRequested += CloseToast; _toast.ActionRequested += OnToastAction;
        NotificationService.InfoBarRequested += OnRequested; NotificationService.NotificationsCleared += OnCleared;
        NotificationService.CenterVisibilityChanged += OnCenterChanged;
        NotificationService.RunningTaskCountChanged += OnRunningCount;
        UpdateBadge(NotificationService.GetRunningTaskCount());
    }
    public void Dispose()
    {
        _disposed = true;
        NotificationService.InfoBarRequested -= OnRequested; NotificationService.NotificationsCleared -= OnCleared;
        NotificationService.CenterVisibilityChanged -= OnCenterChanged; NotificationService.RunningTaskCountChanged -= OnRunningCount;
        _bar.GotFocus -= OnFocusChanged; _bar.LostFocus -= OnFocusChanged;
        _toast.GotFocus -= OnFocusChanged; _toast.LostFocus -= OnFocusChanged;
        _toast.PointerEntered -= OnToastEntered; _toast.PointerExited -= OnToastExited;
        _toast.CloseRequested -= CloseToast; _toast.ActionRequested -= OnToastAction;
        _timer.Stop(); _timer.Tick -= OnTick; _temporary.Clear(); _persistent.Clear(); _bar.ActionButton = null;
    }
    private void OnRequested(NotificationBannerRequest request) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed || !NotificationService.Core.CanQueueBanner(request)) return;
        var scheduler = request.Event.IsPersistent ? _persistent : _temporary;
        scheduler.SetForeground(NotificationService.GetEnvironment().Foreground); scheduler.Enqueue(request); Pump();
    });
    private void OnCleared() => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        _toastHover = _toastFocus = _barHover = _barFocus = false;
        _temporary.Clear(); _persistent.Clear(); Pump();
    });
    private void OnCenterChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        if (NotificationService.IsCenterOpen) { _temporary.DiscardRecorded(); _toastHover = _toastFocus = false; }
        Pump();
    });
    private void OnTick(DispatcherQueueTimer sender, object args) => Pump();
    private void Pump()
    {
        if (_disposed) return;
        try
        {
            var environment = NotificationService.GetEnvironment();
            if (!environment.Enabled || environment.Suppressed) { _temporary.Clear(); _persistent.Clear(); }
            if (environment.CenterOpen) _temporary.DiscardRecorded();
            _temporary.SetForeground(environment.Foreground); _persistent.SetForeground(environment.Foreground);
            _temporary.Advance(); _persistent.Advance();
            RenderToast(); RenderPersistent(environment.Foreground);
            if (_temporary.HasWork || _persistent.HasWork) _timer.Start(); else _timer.Stop();
        }
        catch (Exception error) { _timer.Stop(); LogService.LogError("Could not render notification.", nameof(ShellNotificationPresenter), error); }
    }
    private void RenderToast()
    {
        while (_toastRevision != _temporary.Revision)
        {
            var request = _temporary.Current;
            if (request is not null)
            {
                request = NotificationService.Core.PrepareBanner(request, _toastGroup == request.Event.GroupKey);
                if (request is null || request.QuietUpdate && !request.Resume && _toastGroup != request.Event.GroupKey)
                { _temporary.Close(); continue; }
            }
            _toastGroup = request?.Event.GroupKey;
            if (request is null) _toast.Visibility = Visibility.Collapsed;
            else
            {
                var item = request.Event;
                var action = item.TransientAction is not null ? item.ActionText : item.KeepInCenter
                    ? I18n.GetString(NotificationNavigationService.IsTargetAvailable(item.Target)
                        ? NotificationContentFormatter.ActionResource(item.Target) : "NotificationUI_Details.Content") : null;
                _toast.Render(item, string.IsNullOrWhiteSpace(action) ? I18n.GetString("Notification_Action_View") : action,
                    item.TransientAction is not null || item.KeepInCenter, !request.Resume && !request.QuietUpdate);
                NotificationService.Core.ReportBannerShown(request);
            }
            _toastRevision = _temporary.Revision;
        }
    }
    private void RenderPersistent(bool foreground)
    {
        while (_barRevision != _persistent.Revision)
        {
            var request = _persistent.Current;
            if (request is not null)
            {
                if (!foreground) return;
                request = NotificationService.Core.PrepareBanner(request, _barGroup == request.Event.GroupKey);
                if (request is null) { _persistent.Close(); continue; }
            }
            _rendering = true;
            try
            {
                _bar.IsOpen = false; _bar.ActionButton = null; _barGroup = request?.Event.GroupKey;
                if (request is not null)
                {
                    var item = request.Event; _bar.Title = item.Title; _bar.Message = item.Message;
                    _bar.Severity = item.Severity switch { NotificationSeverity.Error => InfoBarSeverity.Error,
                        NotificationSeverity.Warning => InfoBarSeverity.Warning, NotificationSeverity.Success => InfoBarSeverity.Success, _ => InfoBarSeverity.Informational };
                    if (item.TransientAction is not null || item.KeepInCenter)
                    {
                        var button = new HyperlinkButton { Content = string.IsNullOrWhiteSpace(item.ActionText) ? I18n.GetString("Notification_Action_View") : item.ActionText };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "ShellNotificationAction");
                        button.Click += (_, _) => { _persistent.Close(); Pump(); InvokeAction(item); };
                        _bar.ActionButton = button;
                    }
                    _bar.IsOpen = true; NotificationService.Core.ReportBannerShown(request);
                }
                _barRevision = _persistent.Revision;
            }
            finally { _rendering = false; }
        }
    }
    private void OnToastAction()
    { if (_temporary.Current?.Event is { } item) { CloseToast(); InvokeAction(item); } }
    private static void InvokeAction(NotificationEvent item)
    {
        try
        {
            if (item.KeepInCenter) NotificationCenterService.MarkRead(item.Id, item.Revision);
            if (item.TransientAction is not null) item.TransientAction();
            else if (!NotificationNavigationService.IsTargetAvailable(item.Target) || !NotificationNavigationService.Open(item.Target!))
                NotificationNavigationService.RequestDetails(item.Id);
        }
        catch (Exception error) { LogService.LogError("Notification action failed.", nameof(ShellNotificationPresenter), error); }
    }
    private void CloseToast() { _temporary.Close(); _temporary.SetInteraction(_toastHover, _toastFocus); Pump(); }
    public void GlobalInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    { if (_disposed || _rendering) return; _barHover = _barFocus = false; _persistent.Close(); Pump(); }
    public void GlobalInfoBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    { _barHover = true; _persistent.SetInteraction(_barHover, _barFocus); Pump(); }
    public void GlobalInfoBar_PointerExited(object sender, PointerRoutedEventArgs e)
    { _barHover = false; _persistent.SetInteraction(_barHover, _barFocus); Pump(); }
    private void OnToastEntered(object sender, PointerRoutedEventArgs e)
    { _toastHover = true; _temporary.SetInteraction(_toastHover, _toastFocus); Pump(); }
    private void OnToastExited(object sender, PointerRoutedEventArgs e)
    { _toastHover = false; _temporary.SetInteraction(_toastHover, _toastFocus); Pump(); }
    private void OnFocusChanged(object sender, RoutedEventArgs args) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        _toastFocus = _barFocus = false;
        for (var node = _bar.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
            node is not null; node = VisualTreeHelper.GetParent(node))
        { if (ReferenceEquals(node, _toast)) _toastFocus = true; if (ReferenceEquals(node, _bar)) _barFocus = true; }
        _temporary.SetInteraction(_toastHover, _toastFocus); _persistent.SetInteraction(_barHover, _barFocus); Pump();
    });
    private void OnRunningCount(int count) => _dispatcher.TryEnqueue(() => { if (!_disposed) UpdateBadge(count); });
    private void UpdateBadge(int count)
    { _badge.Value = Math.Max(0, count); _badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed; }
}
