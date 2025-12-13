using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class HistoryPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // 历史列表数据源
        public ObservableCollection<HistoryItem> FilteredHistory { get; set; } = new();

        // 快捷访问配置列表
        public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig?.BackupConfigs ?? new ObservableCollection<BackupConfig>();

        private bool _isEmpty = true;
        public bool IsEmpty
        {
            get => _isEmpty;
            set { _isEmpty = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty))); }
        }

        public HistoryPage()
        {
            this.InitializeComponent();

            // 1. 确保数据服务已初始化
            ConfigService.Initialize();
            HistoryService.Initialize();

            // 2. [关键修复] 显式在代码中设置数据源，防止 XAML 绑定延迟导致 ComboBox 为空
            ConfigFilter.ItemsSource = Configs;
            HistoryList.ItemsSource = FilteredHistory;
        }

        private bool _isNavigating = false;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ManagedFolder folder)
            {
                // 开始导航设置，立起 Flag
                _isNavigating = true;

                try
                {
                    // 1. 找到对应的 Config
                    var targetConfig = Configs.FirstOrDefault(c => c.SourceFolders.Any(f => f.Path == folder.Path));

                    if (targetConfig != null)
                    {
                        // 2. 选中配置
                        ConfigFilter.SelectedItem = targetConfig;

                        // 3. 手动刷新文件夹列表 (因为我们在 SelectionChanged 里屏蔽了逻辑，所以这里要手动做)
                        FolderFilter.ItemsSource = targetConfig.SourceFolders;

                        // 4. 找到并选中对应的 Folder
                        // 注意：这里要用 Path 匹配，确保选中引用正确的对象
                        var targetFolder = targetConfig.SourceFolders.FirstOrDefault(f => f.Path == folder.Path);
                        FolderFilter.SelectedItem = targetFolder;

                        // 5. 刷新历史
                        if (targetFolder != null)
                        {
                            RefreshHistory(targetConfig, targetFolder);
                        }
                    }
                }
                finally
                {
                    // 无论如何，最后放下 Flag
                    _isNavigating = false;
                }
            }
        }

        private void ConfigFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 如果正在进行代码导航设置，直接跳过，不要干扰我的逻辑
            if (_isNavigating) return;

            if (ConfigFilter.SelectedItem is BackupConfig config)
            {
                FolderFilter.ItemsSource = config.SourceFolders;

                // 用户手动点击时，默认选中第一个
                if (config.SourceFolders.Count > 0)
                    FolderFilter.SelectedIndex = 0;
                else
                    FolderFilter.SelectedIndex = -1;
            }
        }

        private void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 确保两个都选中了才刷新
            if (FolderFilter.SelectedItem is ManagedFolder folder && ConfigFilter.SelectedItem is BackupConfig config)
            {
                RefreshHistory(config, folder);
            }
        }

        private void RefreshHistory(BackupConfig config, ManagedFolder folder)
        {
            FilteredHistory.Clear();
            var items = HistoryService.GetHistoryForFolder(config, folder);
            foreach (var item in items)
            {
                FilteredHistory.Add(item);
            }
            IsEmpty = FilteredHistory.Count == 0;
        }

        // 还原按钮点击逻辑
        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            // 获取点击按钮所在的数据行
            if (sender is Button btn && btn.DataContext is HistoryItem item)
            {
                var config = ConfigFilter.SelectedItem as BackupConfig;
                var folder = FolderFilter.SelectedItem as ManagedFolder;

                if (config == null || folder == null) return;

                // 弹出确认对话框
                var dialog = new ContentDialog
                {
                    Title = "确认还原版本",
                    Content = new TextBlock
                    {
                        Text = $"时间：{item.TimeDisplay}\n备注：{item.Comment}\n\n[Clean] 模式：先清空目标文件夹，再还原（推荐，防止残留）。\n[Overwrite] 模式：直接解压覆盖（可能保留旧文件）。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "Clean 还原 (清空目标)",
                    SecondaryButtonText = "Overwrite 还原 (覆盖)",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot // 必须设置 XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    await BackupService.RestoreBackupAsync(config, folder, item, BackupService.RestoreMode.Clean);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    await BackupService.RestoreBackupAsync(config, folder, item, BackupService.RestoreMode.Overwrite);
                }
            }
        }
    }
}