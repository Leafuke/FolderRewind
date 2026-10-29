using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

/// <summary>Compatibility facade. Core policy and state never call Windows APIs under a state lock.</summary>
public static class NotificationService
{
    internal static NotificationCoordinator Core { get; } = new(TimeProvider.System, GetEnvironment,
        new PlatformNotificationSender(), new CompletionNotificationSoundSender());
    internal static event Action<NotificationBannerRequest>? InfoBarRequested
    { add => Core.BannerRequested += value; remove => Core.BannerRequested -= value; }
    internal static event Action? NotificationsCleared;
    internal static event Action? SettingsChanged;
    internal static event Action? CenterVisibilityChanged;
    private static int _centerOpen, _presentationGeneration;
    internal static bool IsCenterOpen => Volatile.Read(ref _centerOpen) != 0;
    internal static void SetCenterOpen(bool open)
    {
        if (Interlocked.Exchange(ref _centerOpen, open ? 1 : 0) == (open ? 1 : 0)) return;
        if (open) Interlocked.Increment(ref _presentationGeneration);
        NotificationCoordinator.Notify(CenterVisibilityChanged);
    }
    public static event Action<int>? BadgeCountChanged;
    public static event Action<int>? RunningTaskCountChanged;
    private static readonly object TrackingLock = new();
    private static bool _tracking;
    private static int _runningTaskCount, _suppressionCount, _generation;
    private const string ReportedExceptionKey = "FolderRewind.NotificationReported";
    internal static bool WasExceptionReported(Exception error)
    { try { return error.Data[ReportedExceptionKey] is true; } catch { return false; } }
    internal static void MarkExceptionReported(Exception error)
    { try { error.Data[ReportedExceptionKey] = true; } catch { /* Notification bookkeeping cannot replace the original failure. */ } }
    internal static int NotificationGeneration => Volatile.Read(ref _generation);
    internal static bool IsNotificationEnabled => (ConfigService.CurrentConfig?.GlobalSettings?.EnableNotifications ?? true)
        && Volatile.Read(ref _suppressionCount) == 0;
    static NotificationService() => Core.Changed += RefreshBadgeVisualState;

    internal static NotificationEnvironment GetEnvironment()
    {
        var settings = ConfigService.CurrentConfig?.GlobalSettings;
        return new(settings?.EnableNotifications ?? true,
            Volatile.Read(ref _suppressionCount) > 0 || NotificationOperationScope.Current?.IsInternal == true,
            NativeWindowActivationService.IsAppForeground(),
            (ToastNotificationLevel)Math.Clamp(settings?.ToastNotificationLevel ?? 2, 0, 3),
            (settings?.CompletionSoundIndex ?? 0) > 0, NotificationGeneration, IsCenterOpen, Volatile.Read(ref _presentationGeneration));
    }
    internal static void RefreshNotificationSettings()
    {
        if (!IsNotificationEnabled)
        {
            Interlocked.Increment(ref _generation);
            NotificationCoordinator.Notify(NotificationsCleared);
        }
        RefreshBadgeVisualState();
        NotificationCoordinator.Notify(SettingsChanged);
    }
    private sealed class SuppressionScope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref _suppressionCount) == 0) RefreshNotificationSettings();
        }
    }
    public static IDisposable SuppressNotifications()
    {
        if (Interlocked.Increment(ref _suppressionCount) == 1) RefreshNotificationSettings();
        return new SuppressionScope();
    }

    public static void ShowInfoBar(InAppNotificationRequest request) => Publish(request, null);
    internal static void ShowNotification(InAppNotificationRequest request, NotificationImportance importance,
        IDictionary<string, string>? arguments = null, string? tag = null, string? group = null)
        => Publish(request, importance, arguments, tag, group);
    private static void Publish(InAppNotificationRequest request, NotificationImportance? importance,
        IDictionary<string, string>? arguments = null, string? tag = null, string? group = null, Uri? logo = null, bool systemOnly = false)
    {
        try
        {
            if (request is null) return;
            var item = NotificationEvent.Capture(request, importance, TimeProvider.System, CloudCommandSecurity.Redact,
                NotificationOperationScope.Current?.Target, arguments, logo, group, systemOnly, systemOnly ? tag : null);
            var publication = Core.PublishAsync(item);
            if (item.KeepInCenter && !GetEnvironment().Suppressed)
            {
                if (NotificationOperationScope.Current is { } operation && Core.Store.Find(item.Id)?.ResultKey == item.ResultKey)
                    operation.ResultReported = true;
                LogService.Log(item.Title + ": " + item.Message,
                    item.Severity == NotificationSeverity.Error ? LogLevel.Error
                        : item.Severity == NotificationSeverity.Warning ? LogLevel.Warning : LogLevel.Info,
                    nameof(NotificationService), operationId: item.Target?.OperationId);
            }
            TaskObserver.Observe(publication, nameof(NotificationService));
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[NotificationService] {error}"); }
    }

    public static void ShowInfoBar(string title, string message, NotificationSeverity severity = NotificationSeverity.Informational,
        int autoCloseMs = -1, Action? action = null, string? actionText = null)
        => ShowInfoBar(new() { Title = title, Message = message, Severity = severity, AutoCloseMs = autoCloseMs, Action = action, ActionText = actionText });
    public static void ShowSuccess(string message, string? title = null, int autoCloseMs = 5000, Action? action = null, string? actionText = null)
        => ShowInfoBar(title ?? I18n.GetString("Notification_Success_Title"), message, NotificationSeverity.Success, autoCloseMs, action, actionText);
    public static void ShowInfo(string message, string? title = null, int autoCloseMs = 5000, Action? action = null, string? actionText = null)
        => ShowInfoBar(title ?? I18n.GetString("Notification_Info_Title"), message, NotificationSeverity.Informational, autoCloseMs, action, actionText);
    public static void ShowWarning(string message, string? title = null, int autoCloseMs = 8000, Action? action = null, string? actionText = null)
        => ShowInfoBar(title ?? I18n.GetString("Notification_Warning_Title"), message, NotificationSeverity.Warning, autoCloseMs, action, actionText);
    public static void ShowError(string message, string? title = null, int autoCloseMs = 12000, Action? action = null, string? actionText = null)
        => ShowNotification(new() { Title = title ?? I18n.GetString("Notification_Error_Title"), Message = message,
            Severity = NotificationSeverity.Error, AutoCloseMs = autoCloseMs, Action = action, ActionText = actionText }, NotificationImportance.Error);
    public static void ShowImportant(string message, string? title = null, int autoCloseMs = 8000, Action? action = null, string? actionText = null)
        => ShowNotification(new() { Title = title ?? I18n.GetString("Notification_Important_Title"), Message = message,
            Severity = NotificationSeverity.Warning, AutoCloseMs = autoCloseMs, Action = action, ActionText = actionText }, NotificationImportance.Important);

    public static void ShowToast(string title, string message, IDictionary<string, string>? arguments = null, string? tag = null,
        string? group = null, NotificationSeverity severity = NotificationSeverity.Informational)
        => Publish(new() { Title = title, Message = message, Severity = severity, Category = NotificationCategory.BusinessResult },
            severity == NotificationSeverity.Error ? NotificationImportance.Error : severity == NotificationSeverity.Warning
                ? NotificationImportance.Important : NotificationImportance.Info, arguments, tag, group, systemOnly: true);
    public static void ShowToastWithLogo(string title, string message, Uri? logoUri = null, IDictionary<string, string>? arguments = null,
        string? tag = null, string? group = null)
        => Publish(new() { Title = title, Message = message, Category = NotificationCategory.BusinessResult },
            NotificationImportance.Info, arguments, tag, group, logoUri, systemOnly: true);

    public static void NotifyBackupCompleted(string folderName, bool success, string? errorMessage = null)
        => ShowNotification(new()
        {
            Category = NotificationCategory.BusinessResult, Source = "backup",
            Title = I18n.GetString(success ? "Notification_BackupCompleted_Title" : "Notification_BackupFailed_Title"),
            Message = success ? I18n.Format("Notification_BackupCompleted_Success", folderName)
                : I18n.Format("Notification_BackupCompleted_Failed", folderName, errorMessage ?? ""),
            Details = errorMessage, Severity = success ? NotificationSeverity.Success : NotificationSeverity.Error,
            CompletionSoundSuccess = success, Target = NotificationOperationScope.Current?.Target ?? new(NotificationTargetKind.Tasks)
        }, success ? NotificationImportance.Info : NotificationImportance.Error);
    public static void NotifyRestoreCompleted(string folderName, bool success, string? errorMessage = null)
    {
        var warning = success && !string.IsNullOrWhiteSpace(errorMessage);
        ShowNotification(new()
        {
            Category = NotificationCategory.BusinessResult, Source = "restore",
            Content = new(NotificationOperationKind.Restore, NotificationTrigger.Manual,
                !success ? NotificationResultKind.Failed : warning ? NotificationResultKind.Warning : NotificationResultKind.Success,
                ConfigService.CurrentConfig?.BackupConfigs?.FirstOrDefault(c => c.Id == NotificationOperationScope.Current?.Target.ConfigId)?.Name ?? "", folderName),
            Title = I18n.GetString("Notification_RestoreCompleted_Title"),
            Message = warning ? I18n.Format("NotificationCenter_RestoreWarning", folderName)
                : success ? I18n.Format("Notification_RestoreCompleted_Success", folderName)
                : I18n.Format("Notification_RestoreCompleted_Failed", folderName, errorMessage ?? ""),
            Details = errorMessage, Severity = !success ? NotificationSeverity.Error : warning ? NotificationSeverity.Warning : NotificationSeverity.Success,
            CompletionSoundSuccess = success, Target = NotificationOperationScope.Current?.Target ?? new(NotificationTargetKind.History)
        }, !success ? NotificationImportance.Error : warning ? NotificationImportance.Important : NotificationImportance.Info);
    }

    public static void InitializeTaskBadgeTracking()
    {
        lock (TrackingLock)
        {
            if (_tracking) return;
            _tracking = true;
            BackupService.TaskRegistry.RunningCountChanged += RefreshRunningTaskBadgeState;
        }
        RefreshRunningTaskBadgeState();
    }
    private static void RefreshRunningTaskBadgeState()
    {
        var next = BackupService.TaskRegistry.RunningCount;
        if (Interlocked.Exchange(ref _runningTaskCount, next) != next) RunningTaskCountChanged?.Invoke(next);
        RefreshBadgeVisualState();
    }
    public static int GetRunningTaskCount() => Volatile.Read(ref _runningTaskCount);
    public static int GetBadgeCount() => Core.Store.ImportantUnreadCount;
    public static void ClearBadge() => RefreshBadgeVisualState();
    public static void RefreshBadgeVisualState()
    {
        try
        {
            if (AppRuntimeInfo.IsPackaged)
            {
                var manager = Microsoft.Windows.BadgeNotifications.BadgeNotificationManager.Current;
                if (!IsNotificationEnabled) manager.ClearBadge();
                else if (GetRunningTaskCount() > 0) manager.SetBadgeAsGlyph(Microsoft.Windows.BadgeNotifications.BadgeNotificationGlyph.Activity);
                else if (GetBadgeCount() > 0) manager.SetBadgeAsCount((uint)GetBadgeCount());
                else manager.ClearBadge();
            }
            BadgeCountChanged?.Invoke(IsNotificationEnabled ? GetBadgeCount() : 0);
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[NotificationBadge] {error}"); }
    }
}
