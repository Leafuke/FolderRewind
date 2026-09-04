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

        private readonly ShellStartupService _startup;
        private readonly ShellNotificationPresenter _notifications;

        public ShellPageViewModel ViewModel { get; } = new();

        public Border AppTitleBarElement => AppTitleBar;

        public GlobalSettings? Settings => ViewModel.Settings;

        public ShellPage()
        {
            this.InitializeComponent();
            _startup = new(() => XamlRoot);
            _notifications = new(GlobalInfoBar, TasksRunningInfoBadge, DispatcherQueue);
            // ShellPage 是当前唯一导航宿主，供 ViewModel/Service 发起跨页跳转。
            NavigationService.Initialize(this);

            ContentFrame.Navigated += ContentFrame_Navigated;

            // 订阅通知服务的 InfoBar 请求
            Loaded += ShellPage_Loaded;
            Unloaded += ShellPage_Unloaded;

        }

        private void ShellPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // 页面卸载时要撤销宿主与事件订阅，避免下次重建后出现重复回调。
            NavigationService.Clear(this);
            Loaded -= ShellPage_Loaded;
            ViewModel.Dispose();

            _startup.Dispose();
            _notifications.Dispose();

            Unloaded -= ShellPage_Unloaded;
        }

        private void ShellPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // 背景图使用显式异步解码，避免在 x:Bind 首次取值时同步访问文件。
            TaskObserver.Observe(ViewModel.RefreshVisualsAsync(), nameof(ShellPage));
        }



        /// <summary>
        /// 处理 InfoBar 请求并接入队列调度
        /// </summary>


















        private void GlobalInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => _notifications?.GlobalInfoBar_Closed(sender, args);
        private void GlobalInfoBar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _notifications?.GlobalInfoBar_PointerEntered(sender, e);
        private void GlobalInfoBar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _notifications?.GlobalInfoBar_PointerExited(sender, e);

        private void NavView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // 延迟恢复面板状态到下一帧，确保 NavigationView 完成初始视觉状态初始化后再设置 IsPaneOpen。
            // 在 Loaded 事件中直接设置 IsPaneOpen 会导致 NavigationViewItem 在面板展开动画期间
            // 测量到错误的宽度（文本被截断、图标右侧被裁剪），因为此时条目尚未经历完整的布局测量周期。
            DispatcherQueue.TryEnqueue(() =>
            {
                _navViewInitialized = true;
                var settings = ViewModel.Settings;
                SyncPaneStateWithDisplayMode(settings);

                // 兜底刷新绑定，确保导航面板状态能及时反映到界面。
                Bindings.Update();

                NavigateTo("Home");

                TaskObserver.Observe(_startup.StartAsync(), nameof(ShellPage));
            });
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
                "GameDiscovery" => typeof(GameDiscoveryPage),
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

        private void PersistPaneState(bool isOpen, NavigationViewDisplayMode displayMode)
        {
            if (displayMode == NavigationViewDisplayMode.Expanded)
                ViewModel.PersistPaneState(isOpen);
        }


        private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            // 初始化完成前忽略显示模式变更：窗口启动调整大小期间可能触发 DisplayMode 从 Compact 跳到 Expanded，
            // 若此时直接设置 IsPaneOpen 会重现 NavView_Loaded 中已修复的测量时序问题。
            if (!_navViewInitialized)
            {
                return;
            }

            SyncPaneStateWithDisplayMode(ViewModel.Settings);
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
            if (sourcePageType == typeof(GameDiscoveryPage)) return "GameDiscovery";
            return null;
        }
    }
}
