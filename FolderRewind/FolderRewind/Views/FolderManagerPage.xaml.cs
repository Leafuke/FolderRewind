using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class FolderManagerPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // 全局配置列表，用于 ComboBox
        public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig.BackupConfigs;

        private BackupConfig _currentConfig;
        public BackupConfig CurrentConfig
        {
            get => _currentConfig;
            set
            {
                if (_currentConfig != value)
                {
                    _currentConfig = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfig)));
                }
            }
        }

        public FolderManagerPage()
        {
            this.InitializeComponent();

            Loaded += (_, __) => TryApplyPendingSelection();
        }

        private string? _pendingFolderPath;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 支持：传入复合导航参数（配置 + 文件夹）
            if (e.Parameter is ManagerNavigationParameter managerParam)
            {
                ApplyManagerNavigation(managerParam);
                return;
            }

            // 兼容：直接传入 Folder（由其它页面导航）
            if (e.Parameter is ManagedFolder folder)
            {
                var parent = Configs.FirstOrDefault(c => c.SourceFolders.Any(f =>
                    f.Path != null && folder.Path != null &&
                    f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)));

                if (parent != null)
                {
                    CurrentConfig = parent;
                    _pendingFolderPath = folder.Path;
                    TryApplyPendingSelection();
                }

                return;
            }

            if (e.Parameter is BackupConfig config)
            {
                // 修复逻辑：根据 ID 在当前的 Configs 列表中查找对应的实例
                // 这样能确保 ComboBox 的选中项与 ItemsSource 中的对象完全匹配
                var match = Configs.FirstOrDefault(c => c.Id == config.Id);
                if (match != null)
                {
                    CurrentConfig = match;
                }
            }
            // 如果没有参数，且列表不为空，且当前未选中任何项，则默认选中第一个
            else if (CurrentConfig == null && Configs.Count > 0)
            {
                CurrentConfig = Configs[0];
            }

            TryApplyPendingSelection();
        }

        private void ApplyManagerNavigation(ManagerNavigationParameter param)
        {
            BackupConfig? match = null;

            if (!string.IsNullOrWhiteSpace(param.ConfigId))
            {
                match = Configs.FirstOrDefault(c => c.Id == param.ConfigId);
            }

            if (match == null && !string.IsNullOrWhiteSpace(param.FolderPath))
            {
                match = Configs.FirstOrDefault(c => c.SourceFolders.Any(f =>
                    f.Path != null && f.Path.Equals(param.FolderPath, StringComparison.OrdinalIgnoreCase)));
            }

            if (match != null)
            {
                CurrentConfig = match;
            }
            else if (CurrentConfig == null && Configs.Count > 0)
            {
                CurrentConfig = Configs[0];
            }

            if (!string.IsNullOrWhiteSpace(param.FolderPath))
            {
                _pendingFolderPath = param.FolderPath;
            }

            TryApplyPendingSelection();
        }

        private void TryApplyPendingSelection()
        {
            if (string.IsNullOrWhiteSpace(_pendingFolderPath)) return;
            if (CurrentConfig?.SourceFolders == null) return;
            if (FolderList == null) return;

            var path = _pendingFolderPath;

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                var target = CurrentConfig.SourceFolders.FirstOrDefault(f =>
                    f.Path != null && f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

                if (target == null) return;

                FolderList.SelectedItem = target;
                FolderList.ScrollIntoView(target);

                _pendingFolderPath = null;
            });
        }

        // --- 交互逻辑 ---

        private void ConfigSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Binding Mode=TwoWay 应该会自动更新 CurrentConfig，
            // 但如果需要额外逻辑（如刷新列表），可以在这里写。
            // 目前因为 ListView 绑定的是 CurrentConfig.SourceFolders，所以会自动刷新。
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = FolderList.SelectedItem != null;
            BackupSelectedButton.IsEnabled = hasSelection;
            HistoryButton.IsEnabled = hasSelection;
        }

        // 添加文件夹 (核心逻辑)
        private async void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            if (CurrentConfig == null) return;

            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            // 获取窗口句柄
            // 假设你在 App.xaml.cs 中保存了 public static Window Window { get; set; }
            // 如果没有，请务必添加，否则这里会报错或需要用反射
            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                // 查重
                if (CurrentConfig.SourceFolders.Any(f => f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)))
                    return;

                var resourceLoader = ResourceLoader.GetForViewIndependentUse();
                var newFolder = new ManagedFolder
                {
                    Path = folder.Path,
                    DisplayName = folder.Name,
                    Description = "",
                    IsFavorite = false,
                    LastBackupTime = resourceLoader.GetString("FolderManager_NeverBackedUp")
                };

                CurrentConfig.SourceFolders.Add(newFolder);
                ConfigService.Save(); // 实时保存
            }
        }

        // 移除文件夹
        private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder)
            {
                CurrentConfig.SourceFolders.Remove(folder);
                ConfigService.Save();
            }
        }

        // 收藏切换
        private void OnFavoriteToggleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                folder.IsFavorite = !folder.IsFavorite;
                ConfigService.Save();
                // 注意：如果 HomePage 显示了收藏列表，它应该会自动更新，或者需要发个消息通知
            }
        }

        // 打开文件夹
        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder)
            {
                // 使用 Explorer 打开文件夹
                System.Diagnostics.Process.Start("explorer.exe", folder.Path);
            }
        }

        // 1. 添加单个
        private async void OnAddSingleFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) AddFolderLogic(folder);
        }

        // 2. 批量添加子文件夹
        private async void OnAddSubFoldersClick(object sender, RoutedEventArgs e)
        {
            var rootFolder = await PickFolderAsync();
            if (rootFolder != null)
            {
                // 遍历一级子目录
                try
                {
                    // 注意：StorageFolder 遍历比较慢，这里用 System.IO 直接遍历路径
                    string[] subDirs = Directory.GetDirectories(rootFolder.Path);
                    foreach (var dir in subDirs)
                    {
                        // 封装成 StorageFolder 接口不太方便，直接传路径给 AddFolderLogic 的重载
                        AddFolderLogic(dir);
                    }
                }
                catch (Exception ex)
                {
                    // 权限错误等
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
            }
        }

        // 通用添加逻辑
        private void AddFolderLogic(StorageFolder folder) => AddFolderLogic(folder.Path, folder.Name);
        private void AddFolderLogic(string path, string name = null)
        {
            if (CurrentConfig == null) return;
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(path);

            if (CurrentConfig.SourceFolders.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return;

            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            var newFolder = new ManagedFolder
            {
                Path = path,
                DisplayName = name,
                LastBackupTime = resourceLoader.GetString("FolderManager_NeverBackedUp")
            };

            // 自动检测 icon.png
            string potentialIcon = Path.Combine(path, "icon.png");
            if (File.Exists(potentialIcon))
            {
                newFolder.CoverImagePath = potentialIcon;
            }

            CurrentConfig.SourceFolders.Add(newFolder);
            ConfigService.Save();
        }

        // 3. 更换图标逻辑
        private async void onChangeIconClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                var picker = new FileOpenPicker();
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");

                if (App._window != null) InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App._window));

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    try
                    {
                        // 目标路径：文件夹下的 icon.png
                        string destPath = Path.Combine(folder.Path, "icon.png");

                        // 复制并覆盖
                        File.Copy(file.Path, destPath, true);

                        // 更新模型 (需要触发 PropertyChanged，强制刷新)
                        folder.CoverImagePath = null; // 先清空触发刷新
                        folder.CoverImagePath = destPath;

                        ConfigService.Save();
                    }
                    catch (Exception ex)
                    {
                        // 提示用户权限不足等
                        System.Diagnostics.Debug.WriteLine($"Copy icon failed: {ex.Message}");
                    }
                }
            }
        }

        // 下面是备份按钮的占位符，将在 Phase 3 实现
        // 备份整个配置
        private async void OnBackupConfigClick(object sender, RoutedEventArgs e)
        {
            if (CurrentConfig == null) return;

            // 禁用按钮防止重复点击
            BackupConfigButton.IsEnabled = false;

            try
            {
                await BackupService.BackupConfigAsync(CurrentConfig);

                // 这里可以用 ContentDialog 提示完成，或者做一个简单的 Toast
                // 暂时用 Debug
                System.Diagnostics.Debug.WriteLine("配置备份完成");
            }
            finally
            {
                BackupConfigButton.IsEnabled = true;
            }
        }

        // 备份选中文件夹
        private async void OnBackupSelectedClick(object sender, RoutedEventArgs e)
        {
            if (CurrentConfig == null) return;
            if (FolderList.SelectedItem is ManagedFolder folder)
            {
                BackupSelectedButton.IsEnabled = false;
                // 获取注释并清理空白
                string comment = CommentBox.Text?.Trim();

                try
                {
                    // 传递 comment
                    await BackupService.BackupFolderAsync(CurrentConfig, folder, comment);

                    // 备份成功后清空注释，防止误用
                    CommentBox.Text = "";
                }
                finally
                {
                    BackupSelectedButton.IsEnabled = true;
                }
            }
        }

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            if (FolderList.SelectedItem is ManagedFolder folder)
            {
                // 必须传递一个包含 Config 和 Folder 的复合对象，或者简单点，
                // 我们传递 Folder，然后在 HistoryPage 里反查 Config (上一轮代码里已包含此逻辑)
                App.Shell.NavigateTo("History", folder);
            }
        }

        // 打开设置弹窗
        private async void OnConfigSettingsClick(object sender, RoutedEventArgs e)
        {
            if (CurrentConfig == null) return;

            var dialog = new ConfigSettingsDialog(CurrentConfig);
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                // 用户点击了保存，我们将改动写入磁盘
                ConfigService.Save();
            }
            else
            {
                // 如果取消了，这里理论上应该回滚改动
                // 但为了简单，由于是 TwoWay 绑定，建议重新加载 ConfigService 
                // 或者在 Dialog 里做临时副本。鉴于目前只是开发阶段，先简单 Save 也没大问题，
                // 只是取消后内存里的值可能还是变了，直到重启。
                // 严谨的做法是在 Dialog 里 Clone 一个 Config 对象进行编辑。
            }
        }

        // 打开日志窗口
        private void OnOpenLogClick(object sender, RoutedEventArgs e)
        {
            // 创建并显示日志窗口
            // 注意：WinUI 3 窗口管理比较原始，最好在 App 类里做一个单例管理
            // 这里为了演示，我们使用简单的 new Window
            var logWindow = new LogWindow();
            logWindow.Activate();
        }

        // 在 FolderManagerPage 类中添加 PickFolderAsync 方法

        private async System.Threading.Tasks.Task<StorageFolder> PickFolderAsync()
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            return await picker.PickSingleFolderAsync();
        }
    }
}