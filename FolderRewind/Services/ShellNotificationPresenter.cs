using FolderRewind.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Services;

internal sealed class ShellNotificationPresenter : IDisposable
{
    private readonly InfoBar GlobalInfoBar;
    private readonly InfoBadge TasksRunningInfoBadge;
    private readonly DispatcherQueue DispatcherQueue;
    private DispatcherQueueTimer? _infoBarTimer;
    private readonly PriorityNotificationQueue<InAppNotificationRequest> _infoBarQueue = new();
    private readonly NotificationCountdown _countdown = new(TimeProvider.System);
    private InAppNotificationRequest? _currentNotification;
    private bool _isPointerHoveringInfoBar;
    private bool _disposed;
    public ShellNotificationPresenter(InfoBar bar, InfoBadge badge, DispatcherQueue dispatcher)
    {
        GlobalInfoBar = bar;
        TasksRunningInfoBadge = badge;
        DispatcherQueue = dispatcher;
        NotificationService.InfoBarRequested += OnInfoBarRequested;
        NotificationService.NotificationsCleared += OnNotificationsCleared;
        NotificationService.RunningTaskCountChanged += OnRunningTaskCountChanged;
        UpdateTasksRunningBadge(NotificationService.GetRunningTaskCount());
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NotificationService.InfoBarRequested -= OnInfoBarRequested;
        NotificationService.NotificationsCleared -= OnNotificationsCleared;
        NotificationService.RunningTaskCountChanged -= OnRunningTaskCountChanged;
        _infoBarTimer?.Stop();
        if (_infoBarTimer is not null) _infoBarTimer.Tick -= InfoBarTimer_Tick;
        _infoBarQueue.Clear();
        GlobalInfoBar.ActionButton = null;
    }
        private void OnInfoBarRequested(InAppNotificationRequest request)
        {
            var generation = NotificationService.NotificationGeneration;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                if (generation != NotificationService.NotificationGeneration) return;
                if (!NotificationService.IsNotificationEnabled)
                {
                    ClearNotifications();
                    return;
                }
                var replacesCurrent = false;
                if (request.Correlation is { } correlation)
                {
                    _infoBarQueue.RemoveWhere(item => item.Correlation?.Group == correlation.Group);
                    replacesCurrent = _currentNotification?.Correlation?.Group == correlation.Group;
                }
                if (_currentNotification == null && !GlobalInfoBar.IsOpen)
                {
                    DisplayNotification(request);
                }
                else
                {
                    EnqueueNotification(request);
                    if (replacesCurrent || _currentNotification is { } current
                        && GetPriority(request) > GetPriority(current))
                    {
                        // 临时结果优先于常驻公告；被抢占的无关消息稍后恢复。
                        if (!replacesCurrent && _currentNotification is { } interrupted)
                            EnqueueNotification(interrupted);
                        GlobalInfoBar.IsOpen = false;
                    }
                }
            });
        }

        private static int GetPriority(InAppNotificationRequest request)
            => request.Severity == NotificationSeverity.Error ? 3
                : request.AutoCloseMs <= 0 ? 0
                : request.Severity == NotificationSeverity.Warning ? 2 : 1;

        private void EnqueueNotification(InAppNotificationRequest request)
            => _infoBarQueue.Enqueue(request, (left, right) => GetPriority(right).CompareTo(GetPriority(left)));

        private void OnNotificationsCleared()
        {
            if (DispatcherQueue.HasThreadAccess) ClearNotifications();
            else DispatcherQueue.TryEnqueue(ClearNotifications);
        }

        private void ClearNotifications()
        {
            if (_disposed) return;
            _infoBarQueue.Clear();
            _infoBarTimer?.Stop();
            _countdown.Start(0);
            _currentNotification = null;
            GlobalInfoBar.ActionButton = null;
            GlobalInfoBar.IsOpen = false;
        }

        private void DisplayNotification(InAppNotificationRequest request)
        {
            _currentNotification = request;
            GlobalInfoBar.Title = request.Title;
            GlobalInfoBar.Message = request.Message;
            GlobalInfoBar.Severity = request.Severity switch
            {
                NotificationSeverity.Success => InfoBarSeverity.Success,
                NotificationSeverity.Warning => InfoBarSeverity.Warning,
                NotificationSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };

            // 如果有操作回调，添加操作按钮
            if (request.Action != null)
            {
                var actionButton = new Button
                {
                    Content = !string.IsNullOrWhiteSpace(request.ActionText)
                        ? request.ActionText
                        : I18n.GetString("Notification_Action_View")
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(actionButton, "ShellNotificationAction");
                actionButton.Click += (s, e) =>
                {
                    GlobalInfoBar.IsOpen = false;
                    try
                    {
                        request.Action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError("InfoBar action failed.", nameof(ShellNotificationPresenter), ex);
                    }
                };
                GlobalInfoBar.ActionButton = actionButton;
            }
            else
            {
                GlobalInfoBar.ActionButton = null;
            }

            GlobalInfoBar.IsOpen = true;
            _infoBarTimer?.Stop();

            _countdown.Start(request.AutoCloseMs);

            if (request.AutoCloseMs > 0 && !_isPointerHoveringInfoBar)
            {
                EnsureInfoBarTimer();
                _infoBarTimer!.Interval = TimeSpan.FromMilliseconds(request.AutoCloseMs);
                _infoBarTimer.Start();
            }
        }

        private void OnRunningTaskCountChanged(int runningTaskCount)
        {
            DispatcherQueue.TryEnqueue(() => { if (!_disposed) UpdateTasksRunningBadge(runningTaskCount); });
        }

        private void UpdateTasksRunningBadge(int runningTaskCount)
        {
            TasksRunningInfoBadge.Value = Math.Max(0, runningTaskCount);
            TasksRunningInfoBadge.Visibility = runningTaskCount > 0
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void EnsureInfoBarTimer()
        {
            if (_infoBarTimer != null)
            {
                return;
            }

            _infoBarTimer = DispatcherQueue.CreateTimer();
            _infoBarTimer.IsRepeating = false;
            _infoBarTimer.Tick += InfoBarTimer_Tick;
        }

        private void InfoBarTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            // Closed 会同步展示下一条并重启同一个计时器，必须先停止本次计时。
            sender.Stop();
            GlobalInfoBar.IsOpen = false;
        }

        public void GlobalInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        {
            _infoBarTimer?.Stop();
            _currentNotification = null;
            _countdown.Start(0);
            GlobalInfoBar.ActionButton = null;

            if (_disposed || !NotificationService.IsNotificationEnabled)
            {
                _infoBarQueue.Clear();
                return;
            }

            if (_infoBarQueue.TryDequeue(out var nextRequest))
            {
                DisplayNotification(nextRequest);
            }
        }

        public void GlobalInfoBar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerHoveringInfoBar = true;
            if (_infoBarTimer?.IsRunning == true)
            {
                _infoBarTimer.Stop();
                _countdown.Pause();
            }
        }

        public void GlobalInfoBar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerHoveringInfoBar = false;
            if (GlobalInfoBar.IsOpen && _countdown.RemainingMilliseconds > 0)
            {
                EnsureInfoBarTimer();
                _countdown.Resume();
                _infoBarTimer!.Interval = TimeSpan.FromMilliseconds(_countdown.RemainingMilliseconds);
                _infoBarTimer.Start();
            }
        }
}
