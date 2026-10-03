using FolderRewind.Models;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System;
using System.Threading;

namespace FolderRewind.Services
{
    /// <summary>
    /// 通知严重程度（用于 InfoBar 显示）
    /// </summary>
    public enum NotificationSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Toast 通知等级阈值 — 控制系统级弹窗通知的发送门槛。
    /// 数值越大越宽松（发送更多 Toast）。
    /// </summary>
    public enum ToastNotificationLevel
    {
        Off = 0,               // 不发送任何系统 Toast
        ErrorOnly = 1,         // 仅错误
        ImportantAndAbove = 2, // 重要通知 + 错误（默认）
        All = 3                // 所有通知
    }

    /// <summary>
    /// 通知重要程度 — 用于判断是否达到 Toast 发送门槛。
    /// </summary>
    public enum NotificationImportance
    {
        Info = 0,       // 一般信息 / 成功提示
        Important = 1,  // 重要通知（如自动备份停止）
        Error = 2       // 错误（备份失败、恢复失败等）
    }

    /// <summary>
    /// 应用内通知请求模型
    /// </summary>
    public sealed class InAppNotificationRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationSeverity Severity { get; set; } = NotificationSeverity.Informational;
        public int AutoCloseMs { get; set; } = 5000;
        public string? ActionText { get; set; }
        public Action? Action { get; set; }
        public DateTime CreatedTime { get; } = DateTime.Now;
    }

    /// <summary>
    /// 综合通知服务：支持 InfoBar (应用内)、AppNotification (系统 Toast)、Badge Notification
    /// 用户可在设置中全局关闭所有提醒，也可单独设置 Toast 通知等级。
    /// </summary>
    public static class NotificationService
    {
        private sealed class NotificationSuppressionScope : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                // 与 SuppressNotifications 的计数配对，支持可重入/嵌套抑制。
                Interlocked.Decrement(ref _suppressionCount);
            }
        }

        // 应用内 InfoBar 回调（由 ShellPage 订阅）
        public static event Action<InAppNotificationRequest>? InfoBarRequested;

        // Badge 计数变更事件
        public static event Action<int>? BadgeCountChanged;

        // 运行中任务数量变更事件
        public static event Action<int>? RunningTaskCountChanged;

        // 当前 Badge 计数
        private static int _badgeCount = 0;
        private static int _runningTaskCount = 0;
        private static int _suppressionCount = 0;
        private static bool _taskBadgeTrackingInitialized;
        private static readonly object TaskTrackingLock = new();
        private static readonly HashSet<BackupTask> TrackedTasks = new();

        /// <summary>
        /// 当前是否启用通知（全局开关）
        /// </summary>
        private static bool IsNotificationEnabled =>
            _suppressionCount <= 0 && (ConfigService.CurrentConfig?.GlobalSettings?.EnableNotifications ?? true);

        public static IDisposable SuppressNotifications()
        {
            // 返回作用域对象，调用方用 using 控制抑制窗口期。
            Interlocked.Increment(ref _suppressionCount);
            return new NotificationSuppressionScope();
        }

        public static void InitializeTaskBadgeTracking()
        {
            lock (TaskTrackingLock)
            {
                if (_taskBadgeTrackingInitialized)
                {
                    return;
                }

                _taskBadgeTrackingInitialized = true;
                BackupService.ActiveTasks.CollectionChanged += OnActiveTasksCollectionChanged;
                foreach (var task in BackupService.ActiveTasks)
                {
                    TrackTask(task);
                }
            }

            RefreshRunningTaskBadgeState();
        }

        /// <summary>
        /// 获取当前 Toast 通知等级设置
        /// </summary>
        private static ToastNotificationLevel GetToastLevel()
        {
            var level = ConfigService.CurrentConfig?.GlobalSettings?.ToastNotificationLevel ?? 2;
            return (ToastNotificationLevel)Math.Clamp(level, 0, 3);
        }

        /// <summary>
        /// 判断给定重要程度是否满足当前 Toast 等级阈值
        /// </summary>
        private static bool ShouldShowToast(NotificationImportance importance)
        {
            if (!IsNotificationEnabled) return false;
            var level = GetToastLevel();
            // 统一在这里做等级判定，避免各调用点散落重复条件。
            return level switch
            {
                ToastNotificationLevel.Off => false,
                ToastNotificationLevel.ErrorOnly => importance >= NotificationImportance.Error,
                ToastNotificationLevel.ImportantAndAbove => importance >= NotificationImportance.Important,
                ToastNotificationLevel.All => true,
                _ => importance >= NotificationImportance.Important
            };
        }

        #region InfoBar（应用内通知）

        /// <summary>
        /// 发送应用内 InfoBar 请求对象
        /// </summary>
        public static void ShowInfoBar(InAppNotificationRequest request)
        {
            if (!IsNotificationEnabled || request == null) return;

            try
            {
                InfoBarRequested?.Invoke(request);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 发送应用内 InfoBar 通知
        /// </summary>
        public static void ShowInfoBar(string title, string message, NotificationSeverity severity = NotificationSeverity.Informational, int autoCloseMs = 5000, Action? action = null, string? actionText = null)
        {
            ShowInfoBar(new InAppNotificationRequest
            {
                Title = title,
                Message = message,
                Severity = severity,
                AutoCloseMs = autoCloseMs,
                Action = action,
                ActionText = actionText
            });
        }

        /// <summary>
        /// 发送成功通知（InfoBar only）
        /// </summary>
        public static void ShowSuccess(string message, string? title = null, int autoCloseMs = 4000, Action? action = null, string? actionText = null)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Success_Title"), message, NotificationSeverity.Success, autoCloseMs, action, actionText);
        }

        /// <summary>
        /// 发送警告通知（InfoBar only）
        /// </summary>
        public static void ShowWarning(string message, string? title = null, int autoCloseMs = 6000, Action? action = null, string? actionText = null)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Warning_Title"), message, NotificationSeverity.Warning, autoCloseMs, action, actionText);
        }

        /// <summary>
        /// 发送错误通知（InfoBar + Toast + Badge）
        /// </summary>
        public static void ShowError(string message, string? title = null, int autoCloseMs = 8000, Action? action = null, string? actionText = null)
        {
            var resolvedTitle = title ?? I18n.GetString("Notification_Error_Title");
            ShowInfoBar(resolvedTitle, message, NotificationSeverity.Error, autoCloseMs, action, actionText);
            IncrementBadge();

            if (ShouldShowToast(NotificationImportance.Error))
            {
                if (AppRuntimeInfo.IsMsiDistribution)
                {
                    App.TryShowTrayNotification(resolvedTitle, message, NotificationSeverity.Error);
                }
                else
                {
                    ShowToast(resolvedTitle, message);
                }
            }
        }

        /// <summary>
        /// 发送信息通知（InfoBar only）
        /// </summary>
        public static void ShowInfo(string message, string? title = null, int autoCloseMs = 5000, Action? action = null, string? actionText = null)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Info_Title"), message, NotificationSeverity.Informational, autoCloseMs, action, actionText);
        }

        /// <summary>
        /// 发送重要通知（InfoBar + Toast，适用于自动备份停止等重要但非错误事件）
        /// </summary>
        public static void ShowImportant(string message, string? title = null, int autoCloseMs = 6000, Action? action = null, string? actionText = null)
        {
            var resolvedTitle = title ?? I18n.GetString("Notification_Important_Title");
            ShowInfoBar(resolvedTitle, message, NotificationSeverity.Warning, autoCloseMs, action, actionText);

            if (ShouldShowToast(NotificationImportance.Important))
            {
                if (AppRuntimeInfo.IsMsiDistribution)
                {
                    App.TryShowTrayNotification(resolvedTitle, message, NotificationSeverity.Warning);
                }
                else
                {
                    ShowToast(resolvedTitle, message);
                }
            }
        }

        #endregion

        #region Toast（系统级弹窗通知）

        /// <summary>
        /// 发送系统 Toast 通知（AppNotification）。
        /// 通常不直接调用，由 ShowError/ShowImportant/NotifyXxx 根据等级自动决定。
        /// </summary>
        public static void ShowToast(string title, string message, IDictionary<string, string>? arguments = null, string? tag = null, string? group = null)
        {
            if (!IsNotificationEnabled) return;

            if (AppRuntimeInfo.IsMsiDistribution || !AppRuntimeInfo.IsPackaged)
            {
                App.TryShowTrayNotification(title, message, NotificationSeverity.Informational);
                return;
            }

            try
            {
                var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

                if (arguments != null)
                {
                    foreach (var kvp in arguments)
                    {
                        builder.AddArgument(kvp.Key, kvp.Value);
                    }
                }

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    builder.SetTag(tag);
                }

                if (!string.IsNullOrWhiteSpace(group))
                {
                    builder.SetGroup(group);
                }

                var notification = builder.BuildNotification();
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Toast failed: {ex.Message}");
                App.TryShowTrayNotification(title, message, NotificationSeverity.Informational);
            }
        }

        /// <summary>
        /// 发送带图标的系统 Toast 通知
        /// </summary>
        public static void ShowToastWithLogo(string title, string message, Uri? logoUri = null, IDictionary<string, string>? arguments = null, string? tag = null, string? group = null)
        {
            if (!IsNotificationEnabled) return;

            if (AppRuntimeInfo.IsMsiDistribution || !AppRuntimeInfo.IsPackaged)
            {
                App.TryShowTrayNotification(title, message, NotificationSeverity.Informational);
                return;
            }

            try
            {
                var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

                if (arguments != null)
                {
                    foreach (var kvp in arguments)
                    {
                        builder.AddArgument(kvp.Key, kvp.Value);
                    }
                }

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    builder.SetTag(tag);
                }

                if (!string.IsNullOrWhiteSpace(group))
                {
                    builder.SetGroup(group);
                }

                if (logoUri != null)
                {
                    builder.SetAppLogoOverride(logoUri);
                }

                var notification = builder.BuildNotification();
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Toast with logo failed: {ex.Message}");
                App.TryShowTrayNotification(title, message, NotificationSeverity.Informational);
            }
        }

        #endregion

        /// <summary>
        /// 设置 Badge 通知计数
        /// </summary>
        /// <param name="count">计数值，0 清除 Badge</param>
        public static void SetBadgeCount(int count)
        {
            _badgeCount = Math.Max(0, count);
            ApplyBadgeVisualState();
            BadgeCountChanged?.Invoke(_badgeCount);
        }

        public static void RefreshBadgeVisualState()
        {
            ApplyBadgeVisualState();
        }

        private static void ApplyBadgeVisualState()
        {
            try
            {
                // 只有在打包模式下才支持 Badge
                if (AppRuntimeInfo.IsPackaged)
                {
                    if (!IsNotificationEnabled)
                    {
                        Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current.ClearBadge();
                    }
                    else if (_runningTaskCount > 0)
                    {
                        Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current.SetBadgeAsGlyph(
                            Microsoft.Windows.BadgeNotifications.BadgeNotificationGlyph.Activity);
                    }
                    else if (_badgeCount > 0)
                    {
                        Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current.SetBadgeAsCount((uint)_badgeCount);
                    }
                    else
                    {
                        Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current.ClearBadge();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Badge failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 增加 Badge 计数
        /// </summary>
        public static void IncrementBadge(int delta = 1)
        {
            SetBadgeCount(_badgeCount + delta);
        }

        /// <summary>
        /// 清除 Badge
        /// </summary>
        public static void ClearBadge()
        {
            SetBadgeCount(0);
        }

        /// <summary>
        /// 获取当前 Badge 计数
        /// </summary>
        public static int GetBadgeCount() => _badgeCount;

        public static int GetRunningTaskCount() => _runningTaskCount;

        /// <summary>
        /// 备份完成通知（根据结果自动选择 InfoBar / Toast / Badge）
        /// </summary>
        public static void NotifyBackupCompleted(string folderName, bool success, string? errorMessage = null)
        {
            if (!IsNotificationEnabled) return;

            CompletionSoundService.PlayConfiguredCompletionSound(success);

            if (success)
            {
                var message = I18n.Format("Notification_BackupCompleted_Success", folderName);
                ShowSuccess(message);

                // 成功通知仅在用户设为 All 且应用在后台时发 Toast
                if (ShouldShowToast(NotificationImportance.Info) && !IsAppForeground())
                {
                    var args = new Dictionary<string, string> { ["target"] = "Home" };
                    ShowToast(I18n.GetString("Notification_BackupCompleted_Title"), message, args, tag: $"backup_{folderName}");
                }
            }
            else
            {
                var message = I18n.Format("Notification_BackupCompleted_Failed", folderName, errorMessage ?? "");
                var resolvedTitle = I18n.GetString("Notification_BackupFailed_Title");
                var args = new Dictionary<string, string> { ["target"] = "Tasks" };

                ShowInfoBar(resolvedTitle, message, NotificationSeverity.Error, 8000);
                IncrementBadge();

                if (ShouldShowToast(NotificationImportance.Error))
                {
                    if (AppRuntimeInfo.IsMsiDistribution)
                    {
                        App.TryShowTrayNotification(resolvedTitle, message, NotificationSeverity.Error);
                    }
                    else
                    {
                        ShowToast(resolvedTitle, message, args, tag: $"backup_{folderName}");
                    }
                }
            }
        }

        /// <summary>
        /// 恢复完成通知
        /// </summary>
        public static void NotifyRestoreCompleted(string folderName, bool success, string? errorMessage = null)
        {
            if (!IsNotificationEnabled) return;

            CompletionSoundService.PlayConfiguredCompletionSound(success);

            if (success)
            {
                var message = I18n.Format("Notification_RestoreCompleted_Success", folderName);
                ShowSuccess(message);

                if (ShouldShowToast(NotificationImportance.Info) && !IsAppForeground())
                {
                    var args = new Dictionary<string, string> { ["target"] = "History" };
                    ShowToast(I18n.GetString("Notification_RestoreCompleted_Title"), message, args, tag: $"restore_{folderName}");
                }
            }
            else
            {
                var message = I18n.Format("Notification_RestoreCompleted_Failed", folderName, errorMessage ?? "");
                var resolvedTitle = I18n.GetString("Notification_Error_Title");
                var args = new Dictionary<string, string> { ["target"] = "History" };

                ShowInfoBar(resolvedTitle, message, NotificationSeverity.Error, 8000);
                IncrementBadge();

                if (ShouldShowToast(NotificationImportance.Error))
                {
                    if (AppRuntimeInfo.IsMsiDistribution)
                    {
                        App.TryShowTrayNotification(resolvedTitle, message, NotificationSeverity.Error);
                    }
                    else
                    {
                        ShowToast(resolvedTitle, message, args, tag: $"restore_{folderName}");
                    }
                }
            }
        }

        /// <summary>
        /// 检查应用是否在前台
        /// </summary>
        private static bool IsAppForeground()
        {
            return MainWindowService.IsMainWindowVisible();
        }

        private static void OnActiveTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            lock (TaskTrackingLock)
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    foreach (var task in TrackedTasks)
                    {
                        task.PropertyChanged -= OnTrackedTaskPropertyChanged;
                    }

                    TrackedTasks.Clear();
                    foreach (var task in BackupService.ActiveTasks)
                    {
                        TrackTask(task);
                    }
                }
                else
                {
                    if (e.OldItems != null)
                    {
                        foreach (var task in e.OldItems)
                        {
                            if (task is BackupTask removedTask)
                            {
                                UntrackTask(removedTask);
                            }
                        }
                    }

                    if (e.NewItems != null)
                    {
                        foreach (var task in e.NewItems)
                        {
                            if (task is BackupTask newTask)
                            {
                                TrackTask(newTask);
                            }
                        }
                    }
                }
            }

            RefreshRunningTaskBadgeState();
        }

        private static void TrackTask(BackupTask task)
        {
            if (!TrackedTasks.Add(task))
            {
                return;
            }

            task.PropertyChanged += OnTrackedTaskPropertyChanged;
        }

        private static void UntrackTask(BackupTask task)
        {
            if (!TrackedTasks.Remove(task))
            {
                return;
            }

            task.PropertyChanged -= OnTrackedTaskPropertyChanged;
        }

        private static void OnTrackedTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName) && e.PropertyName != nameof(BackupTask.IsCompleted))
            {
                return;
            }

            RefreshRunningTaskBadgeState();
        }

        private static void RefreshRunningTaskBadgeState()
        {
            int nextRunningTaskCount = 0;
            foreach (var task in BackupService.ActiveTasks)
            {
                if (!task.IsCompleted)
                {
                    nextRunningTaskCount++;
                }
            }

            if (_runningTaskCount == nextRunningTaskCount)
            {
                ApplyBadgeVisualState();
                return;
            }

            _runningTaskCount = nextRunningTaskCount;
            ApplyBadgeVisualState();
            RunningTaskCountChanged?.Invoke(_runningTaskCount);
        }
    }
}
