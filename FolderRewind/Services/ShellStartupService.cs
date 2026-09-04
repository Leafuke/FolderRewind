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
        _ => ShowFirstLaunchGuideAsync(),
        _ => ShowKnotLinkCompatibilityDialogAsync(),
        _ => CheckAndNotifyConflictsAsync(),
        _ => Task.WhenAll(CheckAndNotifyNoticeAsync(), CheckAndNotifyUpdateAsync()),
        _ => { CoreFeatureValidationService.TryScheduleInitialValidation(); return Task.CompletedTask; }
    }, _lifetime.Token);
    public void Dispose() { _lifetime.Cancel(); }


        private System.Threading.Tasks.Task<ContentDialogResult> ShowDialogAsync(AppDialogRequest dialog) =>
            AppDialogService.Default.ShowRequestAsync(dialog, _root(), _lifetime.Token);

        /// <summary>
        /// 首次启动引导：中文界面提示视频，其他语言提示官网文档。
        /// </summary>
        private async System.Threading.Tasks.Task ShowFirstLaunchGuideAsync()
        {
            try
            {
                var settings = ConfigService.CurrentConfig?.GlobalSettings;
                if (settings == null || settings.HasShownFirstLaunchGuide) return;

                var dialog = new AppDialogRequest
                {
                    Title = I18n.GetString("FirstLaunch_Title"),
                    Content = I18n.GetString("FirstLaunch_Content"),
                    PrimaryButtonText = I18n.GetString("FirstLaunch_OpenVideo"),
                    CloseButtonText = I18n.GetString("FirstLaunch_Skip"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                var result = await ShowDialogAsync(dialog);
                _lifetime.Token.ThrowIfCancellationRequested();
                await ConfigEditTransaction.ApplyAsync(() => settings.HasShownFirstLaunchGuide = true,
                    () => settings.HasShownFirstLaunchGuide = false, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
                if (result == ContentDialogResult.Primary)
                {
                    await Windows.System.Launcher.LaunchUriAsync(new Uri(OfficialLinksService.GetFirstLaunchGuideUrl()));
                }
            }
            catch (Exception ex)
            {
                LogService.LogError($"[FirstLaunchGuide] {ex.Message}");
            }
        }

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
        /// 后台检查公告，有新公告时通过 InfoBar 通知（非模态），用户可点击查看详情弹窗。
        /// </summary>
        private async System.Threading.Tasks.Task CheckAndNotifyNoticeAsync()
        {
            try
            {
                await NoticeService.CheckForNoticesAsync();

                if (!NoticeService.NewNoticeAvailable) return;

                var preview = NoticeService.NoticeContent.Length > 120
                    ? NoticeService.NoticeContent[..120] + "..."
                    : NoticeService.NoticeContent;

                if (_lifetime.IsCancellationRequested) return;
                NotificationService.ShowInfoBar(
                    I18n.GetString("Notice_DialogTitle"),
                    preview,
                    NotificationSeverity.Informational,
                    autoCloseMs: 0,
                    action: () => TaskObserver.Observe(ShowNoticeContentDialogAsync(), nameof(ShellStartupService)));
            }
            catch (Exception ex)
            {
                LogService.LogError($"[NoticeCheck] {ex.Message}");
            }
        }

        /// <summary>
        /// 显示公告详情 ContentDialog（由 InfoBar 操作按钮触发，仅在用户主动查看时弹出模态框）。
        /// </summary>
        private async System.Threading.Tasks.Task ShowNoticeContentDialogAsync()
        {
            try
            {
                var dialog = new AppDialogRequest
                {
                    Title = I18n.GetString("Notice_DialogTitle"),
                    PrimaryButtonText = I18n.GetString("Notice_DismissButton"),
                    SecondaryButtonText = I18n.GetString("Notice_RemindLaterButton"),
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 400,
                        Content = new TextBlock
                        {
                            Text = NoticeService.NoticeContent,
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    }
                };

                var result = await ShowDialogAsync(dialog);

                if (result == ContentDialogResult.Primary)
                {
                    await NoticeService.MarkAsReadAsync();
                }
                else
                {
                    NoticeService.SnoozeThisSession();
                }
            }
            catch (Exception ex)
            {
                LogService.LogError($"[NoticeDialog] {ex.Message}");
            }
        }

        /// <summary>
        /// 后台检查更新，有新版本时通过 InfoBar 通知（非模态），用户可点击查看详情弹窗。
        /// </summary>
        private async System.Threading.Tasks.Task CheckAndNotifyUpdateAsync()
        {
            try
            {
                var update = await AppUpdateService.CheckForUpdateAsync();
                if (update == null) return;

                var messageLines = new List<string>
                {
                    string.Format("v{0} → v{1}", update.CurrentVersion, update.LatestVersion),
                    update.LatestTag
                };

                if (_lifetime.IsCancellationRequested) return;
                NotificationService.ShowInfoBar(
                    I18n.GetString("Update_Dialog_Title"),
                    string.Join(" | ", messageLines),
                    NotificationSeverity.Informational,
                    autoCloseMs: 0,
                    action: () => TaskObserver.Observe(ShowAppUpdateContentDialogAsync(update), nameof(ShellStartupService)));
            }
            catch (Exception ex)
            {
                LogService.LogError($"[UpdateCheck] {ex.Message}");
            }
        }

        /// <summary>
        /// 显示更新详情 ContentDialog（由 InfoBar 操作按钮触发，仅在用户主动查看时弹出模态框）。
        /// </summary>
        private async System.Threading.Tasks.Task ShowAppUpdateContentDialogAsync(AppUpdateService.UpdateCheckResult update)
        {
            try
            {
                var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                    ? I18n.GetString("Update_Dialog_EmptyNotes")
                    : update.ReleaseNotes;

                var content = string.Format(
                    I18n.GetString("Update_Dialog_Content"),
                    update.CurrentVersion,
                    update.LatestTag,
                    update.LatestVersion,
                    notes);

                if (update.PrimaryAction == UpdatePrimaryAction.OpenStorePage)
                {
                    content += Environment.NewLine + Environment.NewLine + I18n.GetString("Update_Dialog_StoreDelayHint");
                }

                var primaryButtonText = update.PrimaryAction switch
                {
                    UpdatePrimaryAction.OpenStorePage => I18n.GetString("Update_Dialog_OpenStore"),
                    UpdatePrimaryAction.PrepareSideloadPackage => I18n.GetString("Update_Dialog_PrepareSideload"),
                    UpdatePrimaryAction.OpenMsiDownload => I18n.GetString("Update_Dialog_DownloadMsi"),
                    _ => I18n.GetString("Update_Dialog_OpenRelease")
                };

                var dialog = new AppDialogRequest
                {
                    Title = I18n.GetString("Update_Dialog_Title"),
                    Content = new ScrollViewer
                    {
                        MaxHeight = 420,
                        Content = new TextBlock
                        {
                            Text = content,
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    },
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = I18n.GetString("Update_Dialog_Later"),
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await ShowDialogAsync(dialog);
                if (result != ContentDialogResult.Primary) return;

                if (update.PrimaryAction == UpdatePrimaryAction.PrepareSideloadPackage)
                {
                    var prepare = await AppSideloadUpdateService.PrepareUpdateAsync(update);
                    if (!prepare.Success)
                    {
                        var failedDialog = new AppDialogRequest
                        {
                            Title = I18n.GetString("Update_Prepare_Title"),
                            Content = string.Format(I18n.GetString("Update_Prepare_Failed"), prepare.ErrorMessage),
                            CloseButtonText = I18n.GetString("Common_Ok"),
                            DefaultButton = ContentDialogButton.Close
                        };

                        await ShowDialogAsync(failedDialog);
                        return;
                    }

                    var successDialog = new AppDialogRequest
                    {
                        Title = I18n.GetString("Update_Prepare_Title"),
                        Content = string.Format(
                            I18n.GetString("Update_Prepare_Success"),
                            prepare.SourceDisplayName,
                            prepare.InstallScriptPath),
                        PrimaryButtonText = I18n.GetString("Update_Prepare_OpenFolder"),
                        CloseButtonText = I18n.GetString("Common_Ok"),
                        DefaultButton = ContentDialogButton.Primary
                    };

                    var prepareResult = await ShowDialogAsync(successDialog);
                    if (prepareResult == ContentDialogResult.Primary)
                    {
                        if (!ShellPathService.TryRevealPathInExplorer(prepare.InstallScriptPath, out var revealError))
                        {
                            var revealFailedDialog = new AppDialogRequest
                            {
                                Title = I18n.GetString("Update_Prepare_Title"),
                                Content = string.Format(
                                    I18n.GetString("Update_Prepare_OpenFolderFailed"),
                                    string.IsNullOrWhiteSpace(revealError) ? I18n.GetString("Common_Failed") : revealError),
                                CloseButtonText = I18n.GetString("Common_Ok"),
                                DefaultButton = ContentDialogButton.Close
                            };

                            await ShowDialogAsync(revealFailedDialog);
                        }
                    }

                    return;
                }

                var targetUrl = string.IsNullOrWhiteSpace(update.PrimaryActionUrl)
                    ? update.ReleaseUrl
                    : update.PrimaryActionUrl;

                await Windows.System.Launcher.LaunchUriAsync(new Uri(targetUrl));
            }
            catch (Exception ex)
            {
                LogService.LogError($"[UpdateDialog] {ex.Message}");
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
                NotificationService.ShowInfoBar(
                    I18n.GetString("ShellPage_FolderConflict_Title"),
                    string.Join(Environment.NewLine, lines),
                    NotificationSeverity.Warning,
                    autoCloseMs: 0,
                    action: () => TaskObserver.Observe(ShowConflictDetailDialogAsync(), nameof(ShellStartupService)));
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
