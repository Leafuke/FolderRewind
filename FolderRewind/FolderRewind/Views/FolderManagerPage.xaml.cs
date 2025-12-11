using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class FolderManagerPage : Page
    {
        // 绑定全局配置
        public ObservableCollection<BackupConfig> AllConfigs => MockDataService.AllConfigs;
        // 当前显示的文件夹列表
        public ObservableCollection<ManagedFolder> CurrentFolders { get; set; } = new();

        // 用于判断是否处于无配置状态
        private bool _isInitialized = false;

        public FolderManagerPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 1. 错误处理：如果没有配置，禁用 UI 或显示空状态
            if (AllConfigs.Count == 0)
            {
                ShowErrorDialog("没有可用的配置", "请先在主页创建一个备份配置。");
                // 也可以在这里控制 UI 显示一个 "Empty State" 的面板
                return;
            }

            _isInitialized = false; // 暂停事件处理

            BackupConfig targetConfig = null;

            // 2. 路由参数处理
            if (e.Parameter is BackupConfig paramConfig)
            {
                // 尝试在现有列表中找到对应的对象（确保引用一致）
                targetConfig = AllConfigs.FirstOrDefault(c => c.Id == paramConfig.Id);
            }

            // 如果没传参，或没找到，默认选第一个
            if (targetConfig == null)
            {
                targetConfig = AllConfigs.FirstOrDefault();
            }

            // 3. 设置 ComboBox 选中项
            if (targetConfig != null)
            {
                ConfigSelector.SelectedItem = targetConfig;
                LoadFoldersForConfig(targetConfig);
            }

            _isInitialized = true; // 恢复事件处理
        }

        private void ConfigSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;

            // 4. 空值检查，防止崩溃
            if (ConfigSelector.SelectedItem is BackupConfig selectedConfig)
            {
                LoadFoldersForConfig(selectedConfig);
            }
        }

        private void LoadFoldersForConfig(BackupConfig config)
        {
            CurrentFolders.Clear();
            if (config == null) return;

            foreach (var folder in config.Folders)
            {
                CurrentFolders.Add(folder);
            }
        }

        // 5. 收藏切换逻辑
        private void OnToggleFavorite(object sender, RoutedEventArgs e)
        {
            // 获取点击的 ToggleButton 的数据上下文
            if ((sender as FrameworkElement)?.DataContext is ManagedFolder folder)
            {
                // UI绑定是 TwoWay 的，这里其实数据已经变了
                // 但如果需要额外操作（比如立即保存数据库），写在这里

                // 由于引用的是 MockDataService 里的同一个对象，
                // HomePage 在 OnNavigatedTo 时刷新列表就能看到变化。
            }
        }

        // 6. 添加文件夹逻辑
        private async void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            var currentConfig = ConfigSelector.SelectedItem as BackupConfig;
            if (currentConfig == null) return;

            // 模拟文件夹选择器
            // 实际代码请使用 Windows.Storage.Pickers.FolderPicker

            // 这里为了演示，直接添加
            var newFolder = new ManagedFolder
            {
                DisplayName = $"新文件夹 {currentConfig.Folders.Count + 1}",
                FullPath = $@"C:\Data\Folder_{DateTime.Now.Second}",
                StatusText = "等待扫描",
                IsFavorite = false
            };

            // 添加到数据源
            currentConfig.Folders.Add(newFolder);
            // 刷新当前视图
            CurrentFolders.Add(newFolder);
        }

        private async void ShowErrorDialog(string title, string content)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "返回主页",
                XamlRoot = this.XamlRoot
            };

            await dialog.ShowAsync();

            // 引导用户回主页
            if (this.Frame.CanGoBack) this.Frame.GoBack();
            else this.Frame.Navigate(typeof(HomePage));
        }

        // 更换图片逻辑保持不变...
        private void OnChangeImageClick(object sender, RoutedEventArgs e) { /* ... */ }
    }
}