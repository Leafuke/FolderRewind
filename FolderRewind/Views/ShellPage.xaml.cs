using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class ShellPage : Page, INavigationHost
    {
        private bool _isSyncingSelection;
        private bool _isSyncingPaneState;
        private bool _navViewInitialized;
        private DispatcherQueueTimer? _infoBarTimer;
        private bool _startupDialogsStarted;

        public ShellPageViewModel ViewModel { get; } = new();

        public Border AppTitleBarElement => AppTitleBar;

        public GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public ShellPage()
        {
            this.InitializeComponent();
            // ShellPage 是当前唯一导航宿主，供 ViewModel/Service 发起跨页跳转。
            NavigationService.Initialize(this);

            ContentFrame.Navigated += ContentFrame_Navigated;

            // 订阅通知服务的 InfoBar 请求
            NotificationService.InfoBarRequested += OnInfoBarRequested;
            NotificationService.RunningTaskCountChanged += OnRunningTaskCountChanged;
            ConfigService.Saved += OnConfigSaved;
            Unloaded += ShellPage_Unloaded;

            UpdateTasksRunningBadge(NotificationService.GetRunningTaskCount());
        }

        private void ShellPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // 页面卸载时要撤销宿主与事件订阅，避免下次重建后出现重复回调。
            NavigationService.Clear(this);
            NotificationService.InfoBarRequested -= OnInfoBarRequested;
            NotificationService.RunningTaskCountChanged -= OnRunningTaskCountChanged;
            ConfigService.Saved -= OnConfigSaved;
            ViewModel.Dispose();

            if (_infoBarTimer != null)
            {
                _infoBarTimer.Stop();
                _infoBarTimer.Tick -= InfoBarTimer_Tick;
                _infoBarTimer = null;
            }

            Unloaded -= ShellPage_Unloaded;
        }

        private void OnConfigSaved()
        {
            // 配置变更后可能首次满足自检条件，延迟调度由服务内部去重。
            CoreFeatureValidationService.TryScheduleInitialValidation();
        }

        /// <summary>
        /// 处理 InfoBar 请求
        /// </summary>
        private void OnInfoBarRequested(string title, string message, NotificationSeverity severity, int autoCloseMs, Action? action)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                GlobalInfoBar.Title = title;
                GlobalInfoBar.Message = message;
                GlobalInfoBar.Severity = severity switch
                {
                    NotificationSeverity.Success => InfoBarSeverity.Success,
                    NotificationSeverity.Warning => InfoBarSeverity.Warning,
                    NotificationSeverity.Error => InfoBarSeverity.Error,
                    _ => InfoBarSeverity.Informational
                };

                // 如果有操作回调，添加操作按钮
                if (action != null)
                {
                    var actionButton = new Button { Content = I18n.GetString("Notification_Action_View") };
                    actionButton.Click += (s, e) => action?.Invoke();
                    GlobalInfoBar.ActionButton = actionButton;
                }
                else
                {
                    GlobalInfoBar.ActionButton = null;
                }

                GlobalInfoBar.IsOpen = true;

                // 新消息到达时先停掉旧计时器，避免旧自动关闭任务误伤当前消息。
                _infoBarTimer?.Stop();

                // 自动关闭
                if (autoCloseMs > 0)
                {
                    EnsureInfoBarTimer();
                    _infoBarTimer!.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
                    _infoBarTimer.Start();
                }
            });
        }

        private void OnRunningTaskCountChanged(int runningTaskCount)
        {
            DispatcherQueue.TryEnqueue(() => UpdateTasksRunningBadge(runningTaskCount));
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
            GlobalInfoBar.IsOpen = false;
            sender.Stop();
        }

        private void GlobalInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        {
            _infoBarTimer?.Stop();
        }

        private void NavView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // 延迟恢复面板状态到下一帧，确保 NavigationView 完成初始视觉状态初始化后再设置 IsPaneOpen。
            // 在 Loaded 事件中直接设置 IsPaneOpen 会导致 NavigationViewItem 在面板展开动画期间
            // 测量到错误的宽度（文本被截断、图标右侧被裁剪），因为此时条目尚未经历完整的布局测量周期。
            DispatcherQueue.TryEnqueue(() =>
            {
                _navViewInitialized = true;
                var settings = ConfigService.CurrentConfig?.GlobalSettings;
                SyncPaneStateWithDisplayMode(settings);

                // 兜底刷新绑定，确保导航面板状态能及时反映到界面。
                Bindings.Update();

                NavigateTo("Home");

                if (!_startupDialogsStarted)
                {
                    // 启动弹窗链只跑一次，避免返回 Shell 时重复打断用户。
                    _ = RunStartupDialogsAsync();
                }
            });
        }

        private async System.Threading.Tasks.Task RunStartupDialogsAsync()
        {
            if (_startupDialogsStarted)
            {
                return;
            }

            _startupDialogsStarted = true;

            // Let the Shell settle before showing any dialogs or notifications.
            await System.Threading.Tasks.Task.Delay(300);

            // 1. First-launch guide — stays modal (requires explicit user choice).
            await ShowFirstLaunchGuideAsync();

            // 2-4. Fire-and-forget background checks. Results surface as non-blocking
            // InfoBar notifications instead of modal dialogs. Each InfoBar carries an
            // action button that opens the full detail dialog on user demand.
            _ = System.Threading.Tasks.Task.Run(CheckAndNotifyConflictsAsync);
            _ = System.Threading.Tasks.Task.Run(CheckAndNotifyNoticeAsync);
            _ = System.Threading.Tasks.Task.Run(CheckAndNotifyUpdateAsync);

            // Core feature validation already carries its own 2-second grace delay.
            CoreFeatureValidationService.TryScheduleInitialValidation();
        }

        private async System.Threading.Tasks.Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                dialog.XamlRoot ??= this.XamlRoot;
                ThemeService.ApplyThemeToDialog(dialog);
                return await dialog.ShowAsync();
            }

            var tcs = new System.Threading.Tasks.TaskCompletionSource<ContentDialogResult>();
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // 统一在 UI 线程补全 XamlRoot 与主题，避免跨线程弹窗异常。
                    dialog.XamlRoot ??= this.XamlRoot;
                    ThemeService.ApplyThemeToDialog(dialog);
                    tcs.TrySetResult(await dialog.ShowAsync());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Unable to enqueue startup dialog."));
            }

            return await tcs.Task;
        }

        /// <summary>
        /// 首次启动引导：中文界面提示视频，其他语言提示官网文档。
        /// </summary>
        private async System.Threading.Tasks.Task ShowFirstLaunchGuideAsync()
        {
            try
            {
                var settings = ConfigService.CurrentConfig?.GlobalSettings;
                if (settings == null || settings.HasShownFirstLaunchGuide) return;

                settings.HasShownFirstLaunchGuide = true;
                ConfigService.Save();

                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("FirstLaunch_Title"),
                    Content = I18n.GetString("FirstLaunch_Content"),
                    PrimaryButtonText = I18n.GetString("FirstLaunch_OpenVideo"),
                    CloseButtonText = I18n.GetString("FirstLaunch_Skip"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                var result = await ShowDialogAsync(dialog);
                if (result == ContentDialogResult.Primary)
                {
                    await Windows.System.Launcher.LaunchUriAsync(new Uri(OfficialLinksService.GetFirstLaunchGuideUrl()));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FirstLaunchGuide] {ex.Message}");
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

                NotificationService.ShowInfoBar(
                    I18n.GetString("Notice_DialogTitle"),
                    preview,
                    NotificationSeverity.Informational,
                    autoCloseMs: 0,
                    action: () => _ = ShowNoticeContentDialogAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NoticeCheck] {ex.Message}");
            }
        }

        /// <summary>
        /// 显示公告详情 ContentDialog（由 InfoBar 操作按钮触发，仅在用户主动查看时弹出模态框）。
        /// </summary>
        private async System.Threading.Tasks.Task ShowNoticeContentDialogAsync()
        {
            try
            {
                var dialog = new ContentDialog
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
                    NoticeService.MarkAsRead();
                }
                else
                {
                    NoticeService.SnoozeThisSession();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NoticeDialog] {ex.Message}");
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

                NotificationService.ShowInfoBar(
                    I18n.GetString("Update_Dialog_Title"),
                    string.Join(" | ", messageLines),
                    NotificationSeverity.Informational,
                    autoCloseMs: 0,
                    action: () => _ = ShowAppUpdateContentDialogAsync(update));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] {ex.Message}");
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
                    _ => I18n.GetString("Update_Dialog_OpenRelease")
                };

                var dialog = new ContentDialog
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
                        var failedDialog = new ContentDialog
                        {
                            Title = I18n.GetString("Update_Prepare_Title"),
                            Content = string.Format(I18n.GetString("Update_Prepare_Failed"), prepare.ErrorMessage),
                            CloseButtonText = I18n.GetString("Common_Ok"),
                            DefaultButton = ContentDialogButton.Close
                        };

                        await ShowDialogAsync(failedDialog);
                        return;
                    }

                    var successDialog = new ContentDialog
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
                            var revealFailedDialog = new ContentDialog
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
                System.Diagnostics.Debug.WriteLine($"[UpdateDialog] {ex.Message}");
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
                    string.Format("  {0} folder name conflict(s) across configurations.", totalConflicts)
                };

                NotificationService.ShowInfoBar(
                    I18n.GetString("ShellPage_FolderConflict_Title"),
                    string.Join(Environment.NewLine, lines),
                    NotificationSeverity.Warning,
                    autoCloseMs: 0,
                    action: () => _ = ShowConflictDetailDialogAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConflictCheck] {ex.Message}");
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

                var dialog = new ContentDialog
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
                System.Diagnostics.Debug.WriteLine($"[ConflictDialog] {ex.Message}");
            }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_isSyncingSelection)
            {
                return;
            }

            if (args.IsSettingsSelected) NavigateTo("Settings");
            else if (args.SelectedItemContainer?.Tag is string tag) NavigateTo(tag);
        }

        // 公开方法：允许外部强制跳转，并同步选中项
        public void NavigateTo(string pageTag, object? parameter = null)
        {
            Type? pageType = pageTag switch
            {
                "Home" => typeof(HomePage),
                "Manager" => typeof(FolderManagerPage),
                "Tasks" => typeof(BackupTasksPage),
                "History" => typeof(HistoryPage),
                "Logs" => typeof(LogPage),
                "Settings" => typeof(SettingsPage),
                _ => null
            };

            if (pageType != null)
            {
                if (ContentFrame.SourcePageType == pageType && parameter == null)
                {
                    // 同页重复点击时不重建页面，只纠正导航栏选中态和标题。
                    UpdateNavSelection(pageTag);
                    UpdatePageHeader(pageTag);
                    return;
                }

                // 1. 执行跳转
                ContentFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());

                // 2. 同步左侧导航栏的选中状态 (解决你提到的不同步问题)
                UpdateNavSelection(pageTag);

                // 3. 同步页面标题区状态。
                UpdatePageHeader(pageTag);
            }
        }

        private void UpdatePageHeader(string pageTag)
        {
            // 当前版本统一不显示页头，保留方法便于后续恢复定制标题策略。
            NavView.Header = null;
        }

        private void UpdateNavSelection(string pageTag)
        {
            object? targetItem = pageTag == "Settings"
                ? NavView.SettingsItem
                : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag?.ToString() == pageTag);

            if (targetItem == null || ReferenceEquals(NavView.SelectedItem, targetItem))
            {
                return;
            }

            try
            {
                // 选中项变更会触发 SelectionChanged，这里用标记避免自激循环。
                _isSyncingSelection = true;
                NavView.SelectedItem = targetItem;
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }

        private void NavView_PaneOpened(NavigationView sender, object args)
        {
            if (_isSyncingPaneState)
            {
                return;
            }

            PersistPaneState(true, sender.DisplayMode);
        }

        private void NavView_PaneClosed(NavigationView sender, object args)
        {
            if (_isSyncingPaneState)
            {
                return;
            }

            PersistPaneState(false, sender.DisplayMode);
        }

        private static void PersistPaneState(bool isOpen, NavigationViewDisplayMode displayMode)
        {
            // 紧凑/最小模式下的展开是临时面板，不写入持久化状态。
            if (displayMode != NavigationViewDisplayMode.Expanded)
            {
                return;
            }

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return;

            if (settings.IsNavPaneOpen != isOpen)
            {
                settings.IsNavPaneOpen = isOpen;
                ConfigService.Save();
            }
        }

        private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            // 初始化完成前忽略显示模式变更：窗口启动调整大小期间可能触发 DisplayMode 从 Compact 跳到 Expanded，
            // 若此时直接设置 IsPaneOpen 会重现 NavView_Loaded 中已修复的测量时序问题。
            if (!_navViewInitialized)
            {
                return;
            }

            SyncPaneStateWithDisplayMode(ConfigService.CurrentConfig?.GlobalSettings);
        }

        private void SyncPaneStateWithDisplayMode(GlobalSettings? settings)
        {
            var targetIsPaneOpen = NavView.DisplayMode == NavigationViewDisplayMode.Expanded
                ? (settings?.IsNavPaneOpen ?? true)
                : false;

            if (NavView.IsPaneOpen == targetIsPaneOpen)
            {
                return;
            }

            try
            {
                _isSyncingPaneState = true;
                // 进入窄宽度时强制折叠，用户点击汉堡按钮时再临时展开。
                NavView.IsPaneOpen = targetIsPaneOpen;
            }
            finally
            {
                _isSyncingPaneState = false;
            }
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack(new SuppressNavigationTransitionInfo());
            }
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;

            var pageTag = GetPageTagFromType(e.SourcePageType);
            if (pageTag != null)
            {
                UpdateNavSelection(pageTag);
                UpdatePageHeader(pageTag);
            }
        }

        private static string? GetPageTagFromType(Type sourcePageType)
        {
            if (sourcePageType == typeof(HomePage)) return "Home";
            if (sourcePageType == typeof(FolderManagerPage)) return "Manager";
            if (sourcePageType == typeof(BackupTasksPage)) return "Tasks";
            if (sourcePageType == typeof(HistoryPage)) return "History";
            if (sourcePageType == typeof(LogPage)) return "Logs";
            if (sourcePageType == typeof(SettingsPage)) return "Settings";
            return null;
        }
    }
}
