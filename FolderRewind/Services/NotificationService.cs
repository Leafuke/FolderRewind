using System;

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
    /// 综合通知服务：支持 InfoBar (应用内)、AppNotification (系统 Toast)、Badge Notification
    /// 用户可在设置中全局关闭所有提醒，也可单独设置 Toast 通知等级。
    /// </summary>
    public static class NotificationService
    {
        // 应用内 InfoBar 回调（由 ShellPage 订阅）
        public static event Action<string, string, NotificationSeverity, int, Action?>? InfoBarRequested;

        // Badge 计数变更事件
        public static event Action<int>? BadgeCountChanged;

        // 当前 Badge 计数
        private static int _badgeCount = 0;

        /// <summary>
        /// 当前是否启用通知（全局开关）
        /// </summary>
        private static bool IsNotificationEnabled =>
            ConfigService.CurrentConfig?.GlobalSettings?.EnableNotifications ?? true;

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
        /// 发送应用内 InfoBar 通知
        /// </summary>
        public static void ShowInfoBar(string title, string message, NotificationSeverity severity = NotificationSeverity.Informational, int autoCloseMs = 5000, Action? action = null)
        {
            if (!IsNotificationEnabled) return;

            try
            {
                InfoBarRequested?.Invoke(title, message, severity, autoCloseMs, action);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 发送成功通知（InfoBar only）
        /// </summary>
        public static void ShowSuccess(string message, string? title = null, int autoCloseMs = 4000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Success_Title"), message, NotificationSeverity.Success, autoCloseMs);
        }

        /// <summary>
        /// 发送警告通知（InfoBar only）
        /// </summary>
        public static void ShowWarning(string message, string? title = null, int autoCloseMs = 6000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Warning_Title"), message, NotificationSeverity.Warning, autoCloseMs);
        }

        /// <summary>
        /// 发送错误通知（InfoBar + Toast + Badge）
        /// </summary>
        public static void ShowError(string message, string? title = null, int autoCloseMs = 8000)
        {
            var resolvedTitle = title ?? I18n.GetString("Notification_Error_Title");
            ShowInfoBar(resolvedTitle, message, NotificationSeverity.Error, autoCloseMs);
            IncrementBadge();

            if (ShouldShowToast(NotificationImportance.Error))
            {
                ShowToast(resolvedTitle, message);
            }
        }

        /// <summary>
        /// 发送信息通知（InfoBar only）
        /// </summary>
        public static void ShowInfo(string message, string? title = null, int autoCloseMs = 5000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Info_Title"), message, NotificationSeverity.Informational, autoCloseMs);
        }

        /// <summary>
        /// 发送重要通知（InfoBar + Toast，适用于自动备份停止等重要但非错误事件）
        /// </summary>
        public static void ShowImportant(string message, string? title = null, int autoCloseMs = 6000)
        {
            var resolvedTitle = title ?? I18n.GetString("Notification_Important_Title");
            ShowInfoBar(resolvedTitle, message, NotificationSeverity.Warning, autoCloseMs);

            if (ShouldShowToast(NotificationImportance.Important))
            {
                ShowToast(resolvedTitle, message);
            }
        }

        #endregion

        #region Toast（系统级弹窗通知）

        /// <summary>
        /// 发送系统 Toast 通知（AppNotification）。
        /// 通常不直接调用，由 ShowError/ShowImportant/NotifyXxx 根据等级自动决定。
        /// </summary>
        public static void ShowToast(string title, string message)
        {
            if (!IsNotificationEnabled) return;

            try
            {
                var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

                var notification = builder.BuildNotification();
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Toast failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送带图标的系统 Toast 通知
        /// </summary>
        public static void ShowToastWithLogo(string title, string message, Uri? logoUri = null)
        {
            if (!IsNotificationEnabled) return;

            try
            {
                var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

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
            }
        }

        #endregion

        /// <summary>
        /// 设置 Badge 通知计数
        /// </summary>
        /// <param name="count">计数值，0 清除 Badge</param>
        public static void SetBadgeCount(int count)
        {
            if (!IsNotificationEnabled && count > 0) return;

            _badgeCount = Math.Max(0, count);

            try
            {
                // 只有在打包模式下才支持 Badge
                if (IsAppPackaged())
                {
                    if (_badgeCount > 0)
                    {
                        Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current.SetBadgeAsCount((uint)_badgeCount);
                    }
                    else
                    {
                        Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current.ClearBadge();
                    }
                }

                BadgeCountChanged?.Invoke(_badgeCount);
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

        /// <summary>
        /// 备份完成通知（根据结果自动选择 InfoBar / Toast / Badge）
        /// </summary>
        public static void NotifyBackupCompleted(string folderName, bool success, string? errorMessage = null)
        {
            if (!IsNotificationEnabled) return;

            if (success)
            {
                var message = I18n.Format("Notification_BackupCompleted_Success", folderName);
                ShowSuccess(message);

                // 成功通知仅在用户设为 All 且应用在后台时发 Toast
                if (ShouldShowToast(NotificationImportance.Info) && !IsAppForeground())
                {
                    ShowToast(I18n.GetString("Notification_BackupCompleted_Title"), message);
                }
            }
            else
            {
                var message = I18n.Format("Notification_BackupCompleted_Failed", folderName, errorMessage ?? "");
                // ShowError 已内置 Badge 递增 + Toast 发送
                ShowError(message, I18n.GetString("Notification_BackupFailed_Title"));
            }
        }

        /// <summary>
        /// 恢复完成通知
        /// </summary>
        public static void NotifyRestoreCompleted(string folderName, bool success, string? errorMessage = null)
        {
            if (!IsNotificationEnabled) return;

            if (success)
            {
                var message = I18n.Format("Notification_RestoreCompleted_Success", folderName);
                ShowSuccess(message);

                if (ShouldShowToast(NotificationImportance.Info) && !IsAppForeground())
                {
                    ShowToast(I18n.GetString("Notification_RestoreCompleted_Title"), message);
                }
            }
            else
            {
                var message = I18n.Format("Notification_RestoreCompleted_Failed", folderName, errorMessage ?? "");
                ShowError(message, I18n.GetString("Notification_Error_Title"));
            }
        }

        /// <summary>
        /// 检查应用是否打包运行
        /// </summary>
        private static bool IsAppPackaged()
        {
            try
            {
                return Windows.ApplicationModel.Package.Current != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查应用是否在前台
        /// </summary>
        private static bool IsAppForeground()
        {
            try
            {
                var mainWindow = App.MainWindow;
                if (mainWindow?.AppWindow != null)
                {
                    return mainWindow.AppWindow.IsVisible;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
