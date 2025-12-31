using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Linq;
using Microsoft.UI.Xaml.Navigation;

namespace FolderRewind.Views
{
    public sealed partial class ShellPage : Page
    {
        private bool _isSyncingSelection;

        public Border AppTitleBarElement => AppTitleBar;

        public GlobalSettings Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public ShellPage()
        {
            this.InitializeComponent();
            // 注册自己，方便全局调用
            App.Shell = this;

            ContentFrame.Navigated += ContentFrame_Navigated;
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
                ContentFrame.Navigate(pageType, parameter, new SlideNavigationTransitionInfo());

                // 2. 同步左侧导航栏的选中状态 (解决你提到的不同步问题)
                UpdateNavSelection(pageTag);

                // 3. 更新页面 Header (仅 Home 显示 FolderRewind)
                UpdatePageHeader(pageTag);
            }
        }

        private void UpdatePageHeader(string pageTag)
        {
            if (pageTag == "Home")
            {
                NavView.Header = "FolderRewind";
            }
            else
            {
                NavView.Header = null;
            }
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
                ContentFrame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
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