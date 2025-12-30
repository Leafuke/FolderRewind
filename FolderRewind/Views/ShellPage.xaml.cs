using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class ShellPage : Page
    {
        private bool _isSyncingSelection;

        public ShellPage()
        {
            this.InitializeComponent();
            // 注册自己，方便全局调用
            App.Shell = this;
        }

        private void NavView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
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
    }
}