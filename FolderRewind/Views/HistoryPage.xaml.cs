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

            if (e.Parameter is ManagerNavigationParameter managerParam)
            {
                ApplySelectionFromNavigation(managerParam.ConfigId, managerParam.FolderPath);
                return;
            }

            if (e.Parameter is ManagedFolder folder)
            {
                ApplySelectionFromNavigation(null, folder.Path);
            }
        }

        private void ApplySelectionFromNavigation(string? configId, string? folderPath)
        {
            _isNavigating = true;

            try
            {
                BackupConfig? targetConfig = null;
                if (!string.IsNullOrWhiteSpace(configId))
                {
                    targetConfig = Configs.FirstOrDefault(c => c.Id == configId);
                }

                if (targetConfig == null && !string.IsNullOrWhiteSpace(folderPath))
                {
                    targetConfig = Configs.FirstOrDefault(c => c.SourceFolders.Any(f => f.Path == folderPath));
                }

                if (targetConfig == null)
                {
                    return;
                }

                ConfigFilter.SelectedItem = targetConfig;
                FolderFilter.ItemsSource = targetConfig.SourceFolders;

                ManagedFolder? targetFolder = null;
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    targetFolder = targetConfig.SourceFolders.FirstOrDefault(f => f.Path == folderPath);
                }

                FolderFilter.SelectedItem = targetFolder;

                if (targetFolder != null)
                {
                    RefreshHistory(targetConfig, targetFolder);
                }
            }
            finally
            {
                _isNavigating = false;
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