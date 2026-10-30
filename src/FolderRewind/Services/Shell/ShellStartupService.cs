using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class ShellStartupService : IDisposable
{
    private readonly Func<XamlRoot?> _root;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StartupSequence _sequence = new(ex => LogService.LogError("Startup stage failed.", nameof(ShellStartupService), ex));
    public ShellStartupService(Func<XamlRoot?> root) => _root = root;
    public Task StartAsync() => _sequence.RunAsync(new Func<CancellationToken, Task>[]
    {
        token => Task.Delay(300, token),
        _ => RepairMsiPinsAsync(),
        _ => ShowMsiPinMigrationNoticeAsync(),
        _ => ShowKnotLinkCompatibilityDialogAsync(),
        _ => CheckAndNotifyConflictsAsync(),
        token => ProductMessageService.StartAsync(token),
        _ => { CoreFeatureValidationService.TryScheduleInitialValidation(); return Task.CompletedTask; }
    }, _lifetime.Token);
    public void Dispose() { _lifetime.Cancel(); }

    private static Task RepairMsiPinsAsync()
    {
        if (AppRuntimeInfo.IsMsiDistribution)
        {
            try
            {
                var pins = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                TaskbarShortcutRepair.RepairOwnedPins(pins, AppRuntimeInfo.ExecutablePath,
                    error => LogService.LogError("Could not repair an owned taskbar shortcut.", nameof(ShellStartupService), error));
            }
            catch (Exception error) { LogService.LogError("Could not inspect taskbar pins.", nameof(ShellStartupService), error); }
        }
        return Task.CompletedTask;
    }

    private async Task ShowMsiPinMigrationNoticeAsync()
    {
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        if (!AppRuntimeInfo.IsMsiDistribution || settings.MsiShellIdentityNoticeShown) return;
        try
        {
            var pins = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!System.IO.Directory.Exists(pins)) return;
            bool legacy = false;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            var shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(pins, "*.lnk"))
                {
                    object link = ((dynamic)shell).CreateShortcut(file);
                    try
                    {
                        string target = ((dynamic)link).TargetPath;
                        if (string.Equals(System.IO.Path.GetFileName(target), "FolderRewind.exe", StringComparison.OrdinalIgnoreCase) &&
                            ShellShortcutIdentity.GetAppUserModelId(file) != "Leafuke.FolderRewind.Msi") { legacy = true; break; }
                    }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            if (!legacy || _lifetime.IsCancellationRequested) return;
            NotificationService.ShowInfoBar(I18n.GetString("Msi_PinMigration_Title"), I18n.GetString("Msi_PinMigration_Content"),
                NotificationSeverity.Informational, autoCloseMs: 0);
            await ConfigEditTransaction.ApplyAsync(() => settings.MsiShellIdentityNoticeShown = true,
                () => settings.MsiShellIdentityNoticeShown = false, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
        }
        catch (Exception error) { LogService.LogError("Could not check legacy taskbar shortcuts.", nameof(ShellStartupService), error); }
    }


        private System.Threading.Tasks.Task<ContentDialogResult> ShowDialogAsync(AppDialogRequest dialog) =>
            AppDialogService.Default.ShowRequestAsync(dialog, _root(), _lifetime.Token);



        /// <summary>
        /// 每次启动检测本机 KnotLink 服务端。低于最低支持版本或版本未知时，
        /// 强制显示兼容性提醒，但允许用户稍后处理。
        /// </summary>
        private async System.Threading.Tasks.Task ShowKnotLinkCompatibilityDialogAsync()
        {
            try
            {
                var compatibility = KnotLinkServerManagerService.GetServerCompatibilityInfo();
                if (!compatibility.RequiresUpdate) return;

                var currentVersion = string.IsNullOrWhiteSpace(compatibility.CurrentVersion)
                    ? I18n.GetString("KnotLinkCompatibility_UnknownVersion")
                    : compatibility.CurrentVersion;
                var requiredVersion = $"{KnotLinkServerManagerService.MinimumSupportedServerVersion.Major}." +
                    $"{KnotLinkServerManagerService.MinimumSupportedServerVersion.Minor}";

                var dialog = new AppDialogRequest
                {
                    Title = I18n.GetString("KnotLinkCompatibility_DialogTitle"),
                    Content = I18n.Format(
                        "KnotLinkCompatibility_DialogContent",
                        currentVersion,
                        requiredVersion),
                    PrimaryButtonText = I18n.GetString("KnotLinkCompatibility_UpdateNow"),
                    CloseButtonText = I18n.GetString("KnotLinkCompatibility_RemindLater"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                var result = await ShowDialogAsync(dialog);
                if (result != ContentDialogResult.Primary) return;

                try
                {
                    await KnotLinkServerManagerService.DownloadAndLaunchLatestInstallerAsync();
                    NotificationService.ShowInfo(
                        I18n.GetString("SettingsPage_KnotLinkServer_InstallerLaunched"),
                        I18n.GetString("SettingsPage_KnotLink_Title"));
                }
                catch (Exception ex)
                {
                    await ShowKnotLinkUpdateFailureDialogAsync(ex.Message);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError($"[KnotLinkCompatibility] {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task ShowKnotLinkUpdateFailureDialogAsync(string errorMessage)
        {
            var dialog = new AppDialogRequest
            {
                Title = I18n.GetString("KnotLinkCompatibility_UpdateFailedTitle"),
                Content = I18n.Format("KnotLinkCompatibility_UpdateFailedContent", errorMessage),
                PrimaryButtonText = I18n.GetString("KnotLinkCompatibility_OpenReleases"),
                CloseButtonText = I18n.GetString("Common_Close"),
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri(KnotLinkServerManagerService.OfficialReleasesUrl));
            }
        }

        /// <summary>
        /// 后台检查备份槽位命名冲突，有冲突时通过 InfoBar 警告（非模态），用户可点击查看详情。
        /// </summary>
        private async System.Threading.Tasks.Task CheckAndNotifyConflictsAsync()
        {
            try
            {
                var appConfig = ConfigService.CurrentConfig;
                var intraConfigConflicts = FolderNameConflictService.FindIntraConfigConflicts(appConfig);
                var sharedDestinationConflicts = FolderNameConflictService.FindSharedDestinationConflicts(appConfig);

                if (intraConfigConflicts.Count == 0 && sharedDestinationConflicts.Count == 0) return;

                var totalConflicts = intraConfigConflicts.Count + sharedDestinationConflicts.Count;
                var lines = new List<string>
                {
                    I18n.GetString("ShellPage_FolderConflict_Intro"),
                    totalConflicts.ToString(System.Globalization.CultureInfo.CurrentCulture)
                };

                if (_lifetime.IsCancellationRequested) return;
                NotificationService.ShowInfoBar(new InAppNotificationRequest
                {
                    RecordId = "configuration-conflicts", KeepInCenter = true,
                    Title = I18n.GetString("ShellPage_FolderConflict_Title"), Message = string.Join(Environment.NewLine, lines),
                    Severity = NotificationSeverity.Warning, AutoCloseMs = 0,
                    Target = new(NotificationTargetKind.FolderManager),
                    Action = () => TaskObserver.Observe(ShowConflictDetailDialogAsync(), nameof(ShellStartupService))
                });
            }
            catch (Exception ex)
            {
                LogService.LogError($"[ConflictCheck] {ex.Message}");
            }
        }

        /// <summary>
        /// 显示备份槽位冲突详情 ContentDialog（由 InfoBar 操作按钮触发）。
        /// </summary>
        private async System.Threading.Tasks.Task ShowConflictDetailDialogAsync()
        {
            try
            {
                var appConfig = ConfigService.CurrentConfig;
                var intraConfigConflicts = FolderNameConflictService.FindIntraConfigConflicts(appConfig);
                var sharedDestinationConflicts = FolderNameConflictService.FindSharedDestinationConflicts(appConfig);

                if (intraConfigConflicts.Count == 0 && sharedDestinationConflicts.Count == 0) return;

                var lines = new List<string>
                {
                    I18n.GetString("ShellPage_FolderConflict_Intro"),
                    string.Empty
                };

                if (intraConfigConflicts.Count > 0)
                {
                    lines.Add(I18n.GetString("ShellPage_FolderConflict_IntraConfigSection"));
                    foreach (var conflict in intraConfigConflicts)
                    {
                        lines.Add(I18n.Format("ShellPage_FolderConflict_ConfigLine", conflict.ConfigName));
                        lines.Add(I18n.Format("ShellPage_FolderConflict_FolderNamesLine", string.Join(", ", conflict.FolderDisplayNames)));
                        lines.Add(string.Empty);
                    }
                }

                if (sharedDestinationConflicts.Count > 0)
                {
                    lines.Add(I18n.GetString("ShellPage_FolderConflict_SharedDestinationSection"));
                    foreach (var conflict in sharedDestinationConflicts)
                    {
                        lines.Add(I18n.Format("ShellPage_FolderConflict_PathLine", conflict.DestinationPath));
                        lines.Add(I18n.Format("ShellPage_FolderConflict_ConfigsLine", string.Join(", ", conflict.ConfigNames)));
                        lines.Add(I18n.Format("ShellPage_FolderConflict_FolderNamesLine", string.Join(", ", conflict.FolderDisplayNames)));
                        lines.Add(string.Empty);
                    }
                }

                lines.Add(I18n.GetString("ShellPage_FolderConflict_Footer"));

                var dialog = new AppDialogRequest
                {
                    Title = I18n.GetString("ShellPage_FolderConflict_Title"),
                    Content = new ScrollViewer
                    {
                        MaxHeight = 420,
                        Content = new TextBlock
                        {
                            Text = string.Join(Environment.NewLine, lines),
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    },
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    DefaultButton = ContentDialogButton.Close
                };

                await ShowDialogAsync(dialog);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[ConflictDialog] {ex.Message}");
            }
        }


}
