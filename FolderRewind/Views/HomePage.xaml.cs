using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Windows.ApplicationModel.Resources;

namespace FolderRewind.Views
{
    public sealed partial class HomePage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // 收藏夹列表
        public ObservableCollection<ManagedFolder> FavoriteFolders { get; set; } = new();

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> FavoriteFoldersView { get; } = new();

        // 绑定视图（配置卡片）
        public ObservableCollection<object> ConfigsView { get; } = new();

        // 配置列表（业务逻辑仍使用强类型）
        public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig?.BackupConfigs;

        public GlobalSettings Settings => ConfigService.CurrentConfig?.GlobalSettings;

        private bool _isFavoritesEmpty = true;
        public bool IsFavoritesEmpty
        {
            get => _isFavoritesEmpty;
            set { _isFavoritesEmpty = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavoritesEmpty))); }
        }

        public HomePage()
        {
            this.InitializeComponent();

            this.Loaded += OnLoaded;
            FavoriteFolders.CollectionChanged += (_, __) => SyncFavoritesView();
            HookConfigsChanged();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 每次进入首页刷新收藏列表 (因为可能在 Manager 页修改了收藏状态)
            RefreshFavorites();

            // 进入页面时同步一次配置视图
            RefreshConfigsView();
        }

        private void HookConfigsChanged()
        {
            try
            {
                if (ConfigService.CurrentConfig?.BackupConfigs != null)
                {
                    ConfigService.CurrentConfig.BackupConfigs.CollectionChanged -= OnConfigsChanged;
                    ConfigService.CurrentConfig.BackupConfigs.CollectionChanged += OnConfigsChanged;
                }
            }
            catch
            {
                // ignore
            }
        }

        private void OnConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = DispatcherQueue.TryEnqueue(RefreshConfigsView);
        }

        private void RefreshConfigsView()
        {
            ConfigsView.Clear();
            foreach (var cfg in GetSortedConfigs())
            {
                ConfigsView.Add(cfg);
            }
        }

        private IEnumerable<BackupConfig> GetSortedConfigs()
        {
            if (Configs == null) yield break;

            var mode = Settings?.HomeSortMode ?? "NameAsc";
            IEnumerable<BackupConfig> query = Configs;

            query = mode switch
            {
                "NameDesc" => query.OrderByDescending(c => c.Name),
                _ => query.OrderBy(c => c.Name)
            };

            foreach (var cfg in query)
            {
                yield return cfg;
            }
        }

        private void RefreshFavorites()
        {
            FavoriteFolders.Clear();
            if (Configs != null)
            {
                foreach (var config in Configs)
                {
                    foreach (var folder in config.SourceFolders)
                    {
                        if (folder.IsFavorite)
                        {
                            FavoriteFolders.Add(folder);
                        }
                    }
                }
            }

            SyncFavoritesView();
        }

        private void SyncFavoritesView()
        {
            FavoriteFoldersView.Clear();
            foreach (var folder in FavoriteFolders)
            {
                FavoriteFoldersView.Add(folder);
            }

            IsFavoritesEmpty = FavoriteFoldersView.Count == 0;
        }

        // 点击收藏项卡片 -> 跳转到管理页并选中该文件夹
        private void OnFavoriteCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                // 需要找到它属于哪个 Config，才能导航
                var parentConfig = FindParentConfig(folder);
                if (parentConfig != null)
                {
                    // 跳转到管理页并自动选中该文件夹
                    App.Shell.NavigateTo("Manager", ManagerNavigationParameter.ForFolder(parentConfig.Id, folder.Path));
                }
            }
        }

        // 点击配置卡片 -> 跳转到管理页
        private void OnConfigCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is BackupConfig config)
            {
                App.Shell.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));
            }
        }

        private void OnSortModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Settings == null || SortCombo == null) return;

            if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                Settings.HomeSortMode = tag;
                ConfigService.Save();
                RefreshConfigsView();
            }
        }

        // 快速备份按钮点击
        private async void OnQuickBackupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                var parentConfig = FindParentConfig(folder);
                if (parentConfig != null)
                {
                    // 禁用按钮防抖
                    btn.IsEnabled = false;
                    try
                    {
                        // 调用备份服务
                        await BackupService.BackupFolderAsync(parentConfig, folder, "HomePage Quick Backup");
                    }
                    finally
                    {
                        btn.IsEnabled = true;
                    }
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplySortSelection();
        }

        private void ApplySortSelection()
        {
            if (SortCombo == null || Settings == null) return;

            foreach (var obj in SortCombo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is string tag && string.Equals(tag, Settings.HomeSortMode, StringComparison.OrdinalIgnoreCase))
                {
                    SortCombo.SelectedItem = item;
                    return;
                }
            }

            SortCombo.SelectedIndex = 0;
        }

        // 辅助方法：查找文件夹所属的配置
        private BackupConfig FindParentConfig(ManagedFolder folder)
        {
            if (Configs == null) return null;
            return Configs.FirstOrDefault(c => c.SourceFolders.Contains(folder));
        }

        // 添加配置逻辑
        private async void OnAddConfigClick(object sender, RoutedEventArgs e)
        {
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            // (保持之前的逻辑不变)
            var stack = new StackPanel { Spacing = 16 };
            var nameBox = new TextBox { Header = resourceLoader.GetString("HomePage_ConfigNameHeader"), PlaceholderText = resourceLoader.GetString("HomePage_ConfigNamePlaceholder") };

            // 图标选择器
            var iconGrid = new GridView { SelectionMode = ListViewSelectionMode.Single, Height = 100 };
            string[] icons = { "\uE8B7", "\uEB9F", "\uE82D", "\uE943", "\uE77B", "\uEA86" };
            foreach (var icon in icons) iconGrid.Items.Add(icon);
            iconGrid.SelectedIndex = 0;

            iconGrid.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                    <Border Width='40' Height='40' CornerRadius='4' Background='{ThemeResource LayerFillColorDefaultBrush}'>
                        <FontIcon Glyph='{Binding}' FontSize='20' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                    </Border>
                  </DataTemplate>");

            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = resourceLoader.GetString("HomePage_SelectIcon"), Style = (Style)Application.Current.Resources["BaseTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
            stack.Children.Add(iconGrid);

            ContentDialog dialog = new ContentDialog
            {
                Title = resourceLoader.GetString("HomePage_NewConfigDialogTitle"),
                Content = stack,
                PrimaryButtonText = resourceLoader.GetString("HomePage_CreateButton"),
                CloseButtonText = resourceLoader.GetString("HomePage_CancelButton"),
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
                    SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary")
                };
                ConfigService.CurrentConfig.BackupConfigs.Add(newConfig);
                ConfigService.Save();
                App.Shell.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(newConfig.Id));
            }
        }
    }
}