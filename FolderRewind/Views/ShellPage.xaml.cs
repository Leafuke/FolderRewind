using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class ShellPage : Page
    {
        private bool _isSyncingSelection;
        private DispatcherQueueTimer? _infoBarTimer;

        public Border AppTitleBarElement => AppTitleBar;

        public GlobalSettings Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public ShellPage()
        {
            this.InitializeComponent();
            // 注册自己，方便全局调用
            App.Shell = this;

            ContentFrame.Navigated += ContentFrame_Navigated;

            // 订阅通知服务的 InfoBar 请求
            NotificationService.InfoBarRequested += OnInfoBarRequested;
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

                // 自动关闭
                if (autoCloseMs > 0)
                {
                    _infoBarTimer?.Stop();
                    _infoBarTimer = DispatcherQueue.CreateTimer();
                    _infoBarTimer.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
                    _infoBarTimer.IsRepeating = false;
                    _infoBarTimer.Tick += (s, e) =>
                    {
                        GlobalInfoBar.IsOpen = false;
                        _infoBarTimer?.Stop();
                    };
                    _infoBarTimer.Start();
                }
            });
        }

        private void GlobalInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        {
            _infoBarTimer?.Stop();
        }

        private void NavView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings != null)
            {
                NavView.IsPaneOpen = settings.IsNavPaneOpen;
                // Ensure binding updates if settings was null initially (though unlikely here)
                Bindings.Update();
            }

            NavigateTo("Home");

            // 首次启动引导
            _ = ShowFirstLaunchGuideAsync();

            // 启动后台公告检查（参考 MineBackup 的 notice_thread 逻辑）
            _ = CheckAndShowNoticeAsync();

            // 启动检查应用更新（GitHub Release）
            _ = CheckAndShowAppUpdateAsync();
        }

        /// <summary>
        /// 首次启动引导：提示用户查看 Bilibili 介绍视频
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
                ThemeService.ApplyThemeToDialog(dialog);

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.bilibili.com/video/BV1zbcjzhE1y"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FirstLaunchGuide] {ex.Message}");
            }
        }

        /// <summary>
        /// 后台检查公告，有新公告时弹出 ContentDialog。
        /// </summary>
        private async System.Threading.Tasks.Task CheckAndShowNoticeAsync()
        {
            try
            {
                await NoticeService.CheckForNoticesAsync();

                if (!NoticeService.NewNoticeAvailable) return;

                // 在 UI 线程显示对话框
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = I18n.GetString("Notice_DialogTitle"),
                            PrimaryButtonText = I18n.GetString("Notice_DismissButton"),
                            SecondaryButtonText = I18n.GetString("Notice_RemindLaterButton"),
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = this.XamlRoot,
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

                        var result = await dialog.ShowAsync();

                        if (result == ContentDialogResult.Primary)
                        {
                            // "确认并不再提示"
                            NoticeService.MarkAsRead();
                        }
                        else
                        {
                            // "稍后提醒"
                            NoticeService.SnoozeThisSession();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NoticeDialog] {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NoticeCheck] {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task CheckAndShowAppUpdateAsync()
        {
            try
            {
                var update = await AppUpdateService.CheckForUpdateAsync();
                if (update == null) return;

                DispatcherQueue.TryEnqueue(async () =>
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
                            PrimaryButtonText = I18n.GetString("Update_Dialog_OpenRelease"),
                            CloseButtonText = I18n.GetString("Update_Dialog_Later"),
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = this.XamlRoot
                        };
                        ThemeService.ApplyThemeToDialog(dialog);

                        var result = await dialog.ShowAsync();
                        if (result != ContentDialogResult.Primary) return;

                        await Windows.System.Launcher.LaunchUriAsync(new Uri(update.ReleaseUrl));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[UpdateDialog] {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] {ex.Message}");
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
        public void NavigateTo(string pageTag, object parameter = null)
        {
            Type pageType = pageTag switch
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
                    UpdateNavSelection(pageTag);
                    UpdatePageHeader(pageTag);
                    return;
                }

                // 1. 执行跳转
                ContentFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());

                // 2. 同步左侧导航栏的选中状态 (解决你提到的不同步问题)
                UpdateNavSelection(pageTag);

                // 3. 更新页面 Header (仅 Home 显示 FolderRewind)
                UpdatePageHeader(pageTag);
            }
        }

        private void UpdatePageHeader(string pageTag)
        {
            NavView.Header = null;
            //if (pageTag == "Home")
            //{
            //    NavView.Header = "FolderRewind";
            //}
            //else
            //{
            //    NavView.Header = null;
            //}
        }

        private void UpdateNavSelection(string pageTag)
        {
            object targetItem = pageTag == "Settings"
                ? NavView.SettingsItem
                : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag?.ToString() == pageTag);

            if (targetItem == null || ReferenceEquals(NavView.SelectedItem, targetItem))
            {
                return;
            }

            try
            {
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
            PersistPaneState(true);
        }

        private void NavView_PaneClosed(NavigationView sender, object args)
        {
            PersistPaneState(false);
        }

        private static void PersistPaneState(bool isOpen)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return;

            if (settings.IsNavPaneOpen != isOpen)
            {
                settings.IsNavPaneOpen = isOpen;
                ConfigService.Save();
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

        private static string GetPageTagFromType(Type sourcePageType)
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