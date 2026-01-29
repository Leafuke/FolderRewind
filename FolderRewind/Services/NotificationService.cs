using FolderRewind.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 通知严重程度
    /// </summary>
    public enum NotificationSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// 综合通知服务：支持 InfoBar (应用内)、AppNotification (系统 Toast)、Badge Notification
    /// 用户可在设置中全局关闭所有提醒。
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
        /// 发送应用内 InfoBar 通知
        /// </summary>
        /// <param name="title">标题</param>
        /// <param name="message">消息内容</param>
        /// <param name="severity">严重程度</param>
        /// <param name="autoCloseMs">自动关闭时间（毫秒），0 表示不自动关闭</param>
        /// <param name="action">可选的操作按钮回调</param>
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
        /// 发送成功通知
        /// </summary>
        public static void ShowSuccess(string message, string? title = null, int autoCloseMs = 4000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Success_Title"), message, NotificationSeverity.Success, autoCloseMs);
        }

        /// <summary>
        /// 发送警告通知
        /// </summary>
        public static void ShowWarning(string message, string? title = null, int autoCloseMs = 6000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Warning_Title"), message, NotificationSeverity.Warning, autoCloseMs);
        }

        /// <summary>
        /// 发送错误通知
        /// </summary>
        public static void ShowError(string message, string? title = null, int autoCloseMs = 8000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Error_Title"), message, NotificationSeverity.Error, autoCloseMs);
        }

        /// <summary>
        /// 发送信息通知
        /// </summary>
        public static void ShowInfo(string message, string? title = null, int autoCloseMs = 5000)
        {
            ShowInfoBar(title ?? I18n.GetString("Notification_Info_Title"), message, NotificationSeverity.Informational, autoCloseMs);
        }

        /// <summary>
        /// 发送系统 Toast 通知（AppNotification）
        /// </summary>
        /// <param name="title">标题</param>
        /// <param name="message">消息内容</param>
        public static void ShowToast(string title, string message)
        {
            if (!IsNotificationEnabled) return;

            try
            {
                // 使用 Windows App SDK 的 AppNotification API
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
        /// 备份完成通知（综合使用 InfoBar + Toast + Badge）
        /// </summary>
        public static void NotifyBackupCompleted(string folderName, bool success, string? errorMessage = null)
        {
            if (!IsNotificationEnabled) return;

            if (success)
            {
                var message = I18n.Format("Notification_BackupCompleted_Success", folderName);
                ShowSuccess(message);

                // 可选：同时发送 Toast（如果应用在后台）
                if (!IsAppForeground())
                {
                    ShowToast(I18n.GetString("Notification_BackupCompleted_Title"), message);
                }
            }
            else
            {
                var message = I18n.Format("Notification_BackupCompleted_Failed", folderName, errorMessage ?? "");
                ShowError(message);

                // 错误时增加 Badge
                IncrementBadge();

                if (!IsAppForeground())
                {
                    ShowToast(I18n.GetString("Notification_BackupFailed_Title"), message);
                }
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

                if (!IsAppForeground())
                {
                    ShowToast(I18n.GetString("Notification_RestoreCompleted_Title"), message);
                }
            }
            else
            {
                var message = I18n.Format("Notification_RestoreCompleted_Failed", folderName, errorMessage ?? "");
                ShowError(message);
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
