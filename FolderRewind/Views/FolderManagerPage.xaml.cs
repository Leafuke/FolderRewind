using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> ConfigsView { get; } = new();

        // 当前文件夹列表的绑定视图
        public ObservableCollection<object> CurrentFoldersView { get; } = new();

        private GlobalSettings Settings => ConfigService.CurrentConfig?.GlobalSettings;

        private BackupConfig _currentConfig;
        public BackupConfig CurrentConfig
        {
            get => _currentConfig;
            set
            {
                if (_currentConfig != value)
                {
                    UnhookCurrentFoldersChanged(_currentConfig);
                    _currentConfig = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfig)));

                    HookCurrentFoldersChanged(_currentConfig);

                    Bindings.Update();
                    RefreshCurrentFoldersView();

                    PersistManagerSelection(_currentConfig?.Id, null);
                }
            }
        }

        public string SelectedFolderDisplayName
        {
            get
            {
                try
                {
                    if (FolderList?.SelectedItem is ManagedFolder folder && !string.IsNullOrWhiteSpace(folder.DisplayName))
                    {
                        return folder.DisplayName;
                    }
                }
                catch
                {
                    
                }

                return I18n.GetString("FolderManager_NotSelected");
            }
        }

        public FolderManagerPage()
        {
            this.InitializeComponent();

            HookConfigsChanged();
            RefreshConfigsView();

            Loaded += (_, __) => TryApplyPendingSelection();
        }

        private void HookConfigsChanged()
        {
            try
            {
                ConfigService.CurrentConfig.BackupConfigs.CollectionChanged -= OnConfigsChanged;
                ConfigService.CurrentConfig.BackupConfigs.CollectionChanged += OnConfigsChanged;
            }
            catch
            {
                
            }
        }

        private void OnConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                RefreshConfigsView();

                // 如果当前选择的配置被删除，自动切换到列表首项
                if (CurrentConfig != null && !Configs.Contains(CurrentConfig))
                {
                    CurrentConfig = Configs.FirstOrDefault();
                }

                // 如果还没有选中项，尝试恢复上次选择
                if (CurrentConfig == null && Configs.Count > 0)
                {
                    var lastConfigId = Settings?.LastManagerConfigId;
                    CurrentConfig = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(lastConfigId) && c.Id == lastConfigId)
                                   ?? Configs.FirstOrDefault();
                }

                if (CurrentConfig != null && !string.IsNullOrWhiteSpace(Settings?.LastManagerFolderPath))
                {
                    _pendingFolderPath = Settings.LastManagerFolderPath;
                    TryApplyPendingSelection();
                }
            });
        }

        private void RefreshConfigsView()
        {
            ConfigsView.Clear();
            foreach (var cfg in Configs)
            {
                ConfigsView.Add(cfg);
            }
        }

        private void HookCurrentFoldersChanged(BackupConfig? config)
        {
            if (config?.SourceFolders == null) return;
            try
            {
                config.SourceFolders.CollectionChanged -= OnCurrentFoldersChanged;
                config.SourceFolders.CollectionChanged += OnCurrentFoldersChanged;
            }
            catch
            {
                
            }
        }

        private void UnhookCurrentFoldersChanged(BackupConfig? config)
        {
            if (config?.SourceFolders == null) return;
            try
            {
                config.SourceFolders.CollectionChanged -= OnCurrentFoldersChanged;
            }
            catch
            {
                
            }
        }

        private void OnCurrentFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = DispatcherQueue.TryEnqueue(RefreshCurrentFoldersView);
        }

        private void RefreshCurrentFoldersView()
        {
            CurrentFoldersView.Clear();
            if (CurrentConfig?.SourceFolders == null) return;
            foreach (var folder in CurrentConfig.SourceFolders)
            {
                CurrentFoldersView.Add(folder);
            }
        }

        private string? _pendingFolderPath;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
                HotkeyManager.Invoked += HotkeyManager_Invoked;
            }
            catch
            {
            }

            // 进入页面时刷新一次视图集合，避免安装包环境下首次绑定异常
            RefreshConfigsView();

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
                // 优先恢复上次在管理页选择的配置
                var lastConfigId = Settings?.LastManagerConfigId;
                var remembered = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(lastConfigId) && c.Id == lastConfigId);
                CurrentConfig = remembered ?? Configs[0];

                // 如果有记录的文件夹路径，稍后尝试选中
                _pendingFolderPath = Settings?.LastManagerFolderPath;
            }

            RefreshCurrentFoldersView();

            TryApplyPendingSelection();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
            }
            catch
            {
            }
        }

        private async void HotkeyManager_Invoked(object? sender, HotkeyInvokedEventArgs e)
        {
            if (e == null) return;
            if (e.HotkeyId != HotkeyManager.Action_BackupSelectedFolder) return;

            try
            {
                if (FolderList?.SelectedItem is not ManagedFolder folder) return;
                if (CurrentConfig == null) return;

                // [快捷键]
                var baseComment = string.Empty;
                try { baseComment = CommentBox?.Text ?? string.Empty; } catch { }

                var comment = string.IsNullOrWhiteSpace(baseComment)
                    ? "[快捷键]"
                    : (baseComment.Contains("[快捷键]", StringComparison.OrdinalIgnoreCase)
                        ? baseComment
                        : $"{baseComment} [快捷键]");

                // 防止重复触发
                _ = DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (BackupSelectedButton != null && !BackupSelectedButton.IsEnabled) return;
                        if (BackupSelectedButton != null) BackupSelectedButton.IsEnabled = false;

                        await BackupService.BackupFolderAsync(CurrentConfig, folder, comment);
                    }
                    finally
                    {
                        if (BackupSelectedButton != null) BackupSelectedButton.IsEnabled = true;
                    }
                });
            }
            catch
            {
            }
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

                if (target == null)
                {
                    if (Settings != null && string.Equals(Settings.LastManagerFolderPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.LastManagerFolderPath = null;
                        ConfigService.Save();
                    }
                    _pendingFolderPath = null;
                    return;
                }

                FolderList.SelectedItem = target;
                FolderList.ScrollIntoView(target);

                PersistManagerSelection(CurrentConfig?.Id, target.Path);

                _pendingFolderPath = null;
            });
        }

        // --- 交互逻辑 ---

        private void ConfigSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Binding Mode=TwoWay 应该会自动更新 CurrentConfig，
            // 但如果需要额外逻辑（如刷新列表），可以在这里写。
            // 目前因为 ListView 绑定的是 CurrentConfig.SourceFolders，所以会自动刷新。

            if (CurrentConfig != null)
            {
                PersistManagerSelection(CurrentConfig.Id, null);
            }
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = FolderList.SelectedItem != null;
            BackupSelectedButton.IsEnabled = hasSelection;
            HistoryButton.IsEnabled = hasSelection;

            if (hasSelection && FolderList.SelectedItem is ManagedFolder folder)
            {
                PersistManagerSelection(CurrentConfig?.Id, folder.Path);
            }

            Bindings.Update();
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

        // 打开 Mini 悬浮窗
        private void OnOpenMiniWindowClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder && CurrentConfig != null)
            {
                MiniWindowService.Open(CurrentConfig, folder);
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

        // 2.5 插件自动发现（例如 Minecraft：从 .minecraft/versions/*/saves 发现世界）
        private async void OnPluginDiscoverFoldersClick(object sender, RoutedEventArgs e)
        {
            if (CurrentConfig == null) return;

            PluginService.Initialize();

            var rootFolder = await PickFolderAsync();
            if (rootFolder == null) return;

            var discovered = PluginService.InvokeDiscoverManagedFolders(rootFolder.Path);
            if (discovered == null || discovered.Count == 0)
            {
                var rl = ResourceLoader.GetForViewIndependentUse();
                var dialog = new ContentDialog
                {
                    Title = rl.GetString("FolderManager_PluginDiscover_NoResultTitle"),
                    Content = rl.GetString("FolderManager_PluginDiscover_NoResultContent"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            var toAdd = discovered
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Path))
                .Where(f => !CurrentConfig.SourceFolders.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (toAdd.Count == 0)
            {
                var rl = ResourceLoader.GetForViewIndependentUse();
                var dialog = new ContentDialog
                {
                    Title = rl.GetString("FolderManager_PluginDiscover_NoNewTitle"),
                    Content = rl.GetString("FolderManager_PluginDiscover_NoNewContent"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            {
                var rl = ResourceLoader.GetForViewIndependentUse();
                var confirm = new ContentDialog
                {
                    Title = rl.GetString("FolderManager_PluginDiscover_ConfirmTitle"),
                    Content = string.Format(rl.GetString("FolderManager_PluginDiscover_ConfirmContent"), toAdd.Count),
                    PrimaryButtonText = rl.GetString("Common_Ok"),
                    CloseButtonText = rl.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                var res = await confirm.ShowAsync();
                if (res != ContentDialogResult.Primary) return;
            }

            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            foreach (var f in toAdd)
            {
                if (string.IsNullOrWhiteSpace(f.LastBackupTime))
                {
                    f.LastBackupTime = resourceLoader.GetString("FolderManager_NeverBackedUp");
                }
                CurrentConfig.SourceFolders.Add(f);
            }

            ConfigService.Save();
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
            if (CurrentConfig == null) return;
            if (FolderList.SelectedItem is ManagedFolder folder)
            {
                var param = ManagerNavigationParameter.ForFolder(CurrentConfig.Id, folder.Path);
                App.Shell.NavigateTo("History", param);
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

        private void PersistManagerSelection(string configId, string folderPath)
        {
            var settings = Settings;
            if (settings == null) return;

            bool updated = false;

            if (!string.IsNullOrWhiteSpace(configId) && settings.LastManagerConfigId != configId)
            {
                settings.LastManagerConfigId = configId;
                updated = true;
            }

            if (!string.IsNullOrWhiteSpace(folderPath) && settings.LastManagerFolderPath != folderPath)
            {
                settings.LastManagerFolderPath = folderPath;
                updated = true;
            }

            if (updated)
            {
                ConfigService.Save();
            }
        }
    }
}