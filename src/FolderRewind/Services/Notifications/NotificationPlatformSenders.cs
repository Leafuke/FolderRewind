using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FolderRewind.Services;

internal sealed class WindowsNotificationSender : INotificationSystemSender
{
    public Task<NotificationSendStatus> SendAsync(NotificationEvent item, bool muteAudio, Func<bool> canSend)
    {
        try
        {
            if (!canSend()) return Task.FromResult(NotificationSendStatus.Canceled);
            if (!AppNotificationManager.IsSupported()) return Task.FromResult(NotificationSendStatus.Unavailable);
            if (AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled)
                return Task.FromResult(NotificationSendStatus.Disabled);
            var builder = new AppNotificationBuilder().AddText(item.Title).AddText(item.Message);
            var arguments = item.Arguments.ToDictionary(p => p.Key, p => p.Value);
            if (item.KeepInCenter)
                foreach (var pair in NotificationNavigationService.ToArguments(item.Id, item.Target)) arguments[pair.Key] = pair.Value;
            foreach (var pair in arguments) builder.AddArgument(pair.Key, pair.Value);
            builder.SetTag(item.Id);
            if (item.Group is { Length: > 0 } group) builder.SetGroup(group);
            if (item.Logo is not null) builder.SetAppLogoOverride(item.Logo);
            if (muteAudio) builder.MuteAudio();
            if (!canSend()) return Task.FromResult(NotificationSendStatus.Canceled);
            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return Task.FromResult(notification.Id == 0 ? NotificationSendStatus.Failed : NotificationSendStatus.Submitted);
        }
        catch (Exception error)
        {
            LogService.LogWarning("System notification submission failed: " + error.Message, nameof(WindowsNotificationSender));
            return Task.FromResult(NotificationSendStatus.Failed);
        }
    }
}
internal sealed class TrayNotificationSender : INotificationSystemSender
{
    public Task<NotificationSendStatus> SendAsync(NotificationEvent item, bool muteAudio, Func<bool> canSend)
        => App.TryShowTrayNotificationAsync(item.Title, item.Message, item.Severity, muteAudio, canSend);
}
internal sealed class PlatformNotificationSender : INotificationSystemSender
{
    private readonly TrayNotificationSender _tray = new();
    private readonly FallbackNotificationSender _packaged = new(new WindowsNotificationSender(), new TrayNotificationSender());
    public Task<NotificationSendStatus> SendAsync(NotificationEvent item, bool muteAudio, Func<bool> canSend)
        => (AppRuntimeInfo.IsPackaged ? (INotificationSystemSender)_packaged : _tray).SendAsync(item, muteAudio, canSend);
}
internal sealed class CompletionNotificationSoundSender : INotificationSoundSender
{
    public Task<NotificationSendStatus> SendAsync(bool success, Func<bool> canSend)
        => UiDispatcherService.RunOnUiAsync(() =>
        {
            if (!canSend()) return NotificationSendStatus.Canceled;
            CompletionSoundService.PlayConfiguredCompletionSound(success);
            return NotificationSendStatus.Submitted;
        });
}
