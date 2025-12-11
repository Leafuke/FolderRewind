using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;

namespace FolderRewind.Views
{
    public sealed partial class HomePage : Page
    {
        public ObservableCollection<ManagedFolder> FavoriteFolders { get; set; } = new();
        public ObservableCollection<BackupConfig> Configs => MockDataService.AllConfigs;

        public HomePage()
        {
            this.InitializeComponent();
            // 确保有数据，避免空白
            MockDataService.Initialize();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 每次进入页面刷新收藏列表
            RefreshFavorites();
        }

        private void RefreshFavorites()
        {
            FavoriteFolders.Clear();
            var favs = MockDataService.GetFavorites();
            foreach (var f in favs) FavoriteFolders.Add(f);
        }

        // 1. 实现添加配置逻辑
        private async void OnAddConfigClick(object sender, RoutedEventArgs e)
        {
            // 1. 构建复杂的对话框内容
            var stack = new StackPanel { Spacing = 16 };
            var nameBox = new TextBox { Header = "配置名称", PlaceholderText = "例如：工作文档" };

            // 图标选择器
            var iconGrid = new GridView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Height = 100
            };
            string[] icons = { "\uE8B7", "\uEB9F", "\uE82D", "\uE943", "\uE77B", "\uEA86" }; // 常用图标集
            foreach (var icon in icons) iconGrid.Items.Add(icon);
            iconGrid.SelectedIndex = 0;

            // 定义图标样式
            iconGrid.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
            <Border Width='40' Height='40' CornerRadius='4' Background='{ThemeResource LayerFillColorDefaultBrush}'>
                <FontIcon Glyph='{Binding}' FontSize='20' HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
          </DataTemplate>");

            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = "选择图标", Style = (Style)Application.Current.Resources["BaseTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
            stack.Children.Add(iconGrid);

            ContentDialog dialog = new ContentDialog
            {
                Title = "新建备份配置",
                Content = stack,
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
            {
                var selectedIcon = iconGrid.SelectedItem as string ?? "\uE8B7";
                var newConfig = new BackupConfig
                {
                    Name = nameBox.Text,
                    IconGlyph = selectedIcon,
                    SummaryText = "新创建 · 暂无数据"
                };
                MockDataService.AllConfigs.Add(newConfig);

                // 2. 使用 App.Shell 导航，解决左侧栏不同步问题
                App.Shell.NavigateTo("Manager", newConfig);
            }
        }

        private void OnConfigClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BackupConfig config)
            {
                // 关键修复：调用 Shell 的导航方法
                App.Shell.NavigateTo("Manager", config);
            }
        }

        private void OnFavoriteClick(object sender, ItemClickEventArgs e)
        {
            // 收藏点击逻辑...
        }


    }
}