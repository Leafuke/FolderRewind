using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
            var list = Configs.ToList();

            IEnumerable<BackupConfig> query = mode switch
            {
                "NameDesc" => list.OrderByDescending(c => c.Name),
                "LastBackupDesc" => list
                    .OrderByDescending(c => GetConfigLastBackupLocalTimeOrMin(c))
                    .ThenBy(c => c.Name),
                "LastModifiedDesc" => list
                    .OrderByDescending(c => GetConfigLastSourceModifiedUtcOrMin(c))
                    .ThenBy(c => c.Name),
                _ => list.OrderBy(c => c.Name)
            };

            foreach (var cfg in query)
            {
                yield return cfg;
            }
        }

        private static DateTime GetConfigLastBackupLocalTimeOrMin(BackupConfig config)
        {
            if (config?.SourceFolders == null) return DateTime.MinValue;

            DateTime? max = null;
            foreach (var folder in config.SourceFolders)
            {
                var t = TryParseBackupLocalTime(folder?.LastBackupTime);
                if (t.HasValue && (!max.HasValue || t.Value > max.Value))
                {
                    max = t;
                }
            }

            return max ?? DateTime.MinValue;
        }

        private static DateTime GetConfigLastSourceModifiedUtcOrMin(BackupConfig config)
        {
            if (config?.SourceFolders == null) return DateTime.MinValue;

            DateTime? maxUtc = null;
            foreach (var folder in config.SourceFolders)
            {
                var path = folder?.Path;
                if (string.IsNullOrWhiteSpace(path)) continue;

                try
                {
                    var t = Directory.GetLastWriteTimeUtc(path);
                    if (t == DateTime.MinValue || t == DateTime.MaxValue) continue;
                    if (!maxUtc.HasValue || t > maxUtc.Value)
                    {
                        maxUtc = t;
                    }
                }
                catch
                {

                }
            }

            return maxUtc ?? DateTime.MinValue;
        }

        private static DateTime? TryParseBackupLocalTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (DateTime.TryParseExact(value, "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact;
            }

            if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            return null;
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
            PluginService.Initialize();

            var stack = new StackPanel { Spacing = 16 };
            var nameBox = new TextBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigNameHeader"),
                PlaceholderText = resourceLoader.GetString("HomePage_ConfigNamePlaceholder")
            };

            // 配置类型（含插件扩展类型）
            var configTypes = PluginService.GetAllSupportedConfigTypes();
            var typeCombo = new ComboBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigTypeHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var t in configTypes) typeCombo.Items.Add(t);
            typeCombo.SelectedIndex = 0;

            var typeDesc = new TextBlock
            {
                Text = resourceLoader.GetString("HomePage_ConfigTypeDesc"),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };

            // 插件批量创建（用于 Minecraft 扫描 .minecraft/versions/*/saves 等）
            var batchCreateToggle = new ToggleSwitch
            {
                Header = resourceLoader.GetString("HomePage_PluginBatchCreateHeader"),
                OffContent = resourceLoader.GetString("HomePage_PluginBatchCreateOff"),
                OnContent = resourceLoader.GetString("HomePage_PluginBatchCreateOn"),
                IsOn = false
            };

            void RefreshBatchToggleState()
            {
                var selectedType = typeCombo.SelectedItem as string;
                var canBatch = !string.IsNullOrWhiteSpace(selectedType) && !string.Equals(selectedType, "Default", StringComparison.OrdinalIgnoreCase);
                batchCreateToggle.IsEnabled = canBatch;
                if (!canBatch) batchCreateToggle.IsOn = false;

                // 批量创建模式下，名称由插件生成
                nameBox.IsEnabled = !batchCreateToggle.IsOn;
            }

            typeCombo.SelectionChanged += (_, __) => RefreshBatchToggleState();
            batchCreateToggle.Toggled += (_, __) => RefreshBatchToggleState();
            RefreshBatchToggleState();

            // 图标选择器
            var iconGrid = new GridView { SelectionMode = ListViewSelectionMode.Single, Height = 180 };
            foreach (var icon in IconCatalog.ConfigIconGlyphs) iconGrid.Items.Add(icon);
            iconGrid.SelectedIndex = 0;

            iconGrid.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                    <Border Width='40' Height='40' CornerRadius='4' Background='{ThemeResource LayerFillColorDefaultBrush}'>
                        <FontIcon Glyph='{Binding}' FontSize='20' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                    </Border>
                  </DataTemplate>");

            stack.Children.Add(nameBox);
            stack.Children.Add(typeCombo);
            stack.Children.Add(typeDesc);
            stack.Children.Add(batchCreateToggle);
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

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var selectedType = typeCombo.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(selectedType)) selectedType = "Default";

                if (batchCreateToggle.IsOn)
                {
                    var rootFolder = await PickFolderAsync(resourceLoader.GetString("HomePage_PluginBatchCreatePickRootTitle"));
                    if (rootFolder == null) return;

                    var result = PluginService.InvokeCreateConfigs(rootFolder.Path, selectedType);
                    if (!result.Handled || result.CreatedConfigs == null || result.CreatedConfigs.Count == 0)
                    {
                        var failed = new ContentDialog
                        {
                            Title = resourceLoader.GetString("HomePage_PluginBatchCreateFailedTitle"),
                            Content = string.IsNullOrWhiteSpace(result.Message)
                                ? resourceLoader.GetString("HomePage_PluginBatchCreateFailedContent")
                                : result.Message,
                            CloseButtonText = resourceLoader.GetString("Common_Ok"),
                            XamlRoot = this.XamlRoot
                        };
                        await failed.ShowAsync();
                        return;
                    }

                    // 让用户一次性选择目标目录（可选；不选则稍后在配置设置里填）
                    var destFolder = await PickFolderAsync(resourceLoader.GetString("HomePage_PluginBatchCreatePickDestinationTitle"));
                    var destPath = destFolder?.Path;

                    foreach (var c in result.CreatedConfigs)
                    {
                        // 兜底：确保 ConfigType
                        if (string.IsNullOrWhiteSpace(c.ConfigType)) c.ConfigType = selectedType;
                        if (!string.IsNullOrWhiteSpace(destPath)) c.DestinationPath = destPath;
                        c.SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary");
                        ConfigService.CurrentConfig.BackupConfigs.Add(c);
                    }

                    ConfigService.Save();
                    App.Shell.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(result.CreatedConfigs[0].Id));
                    return;
                }

                if (string.IsNullOrWhiteSpace(nameBox.Text)) return;

                var selectedIcon = iconGrid.SelectedItem as string ?? IconCatalog.DefaultConfigIconGlyph;
                var newConfig = new BackupConfig
                {
                    Name = nameBox.Text,
                    IconGlyph = selectedIcon,
                    ConfigType = selectedType,
                    SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary")
                };

                ConfigService.CurrentConfig.BackupConfigs.Add(newConfig);
                ConfigService.Save();
                App.Shell.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(newConfig.Id));
            }
        }

        private async System.Threading.Tasks.Task<StorageFolder?> PickFolderAsync(string title)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            if (!string.IsNullOrWhiteSpace(title))
            {
                picker.SettingsIdentifier = "FolderRewind.HomePage.Picker";
            }

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            return await picker.PickSingleFolderAsync();
        }

        #region Context Menu Handlers

        // 右键点击配置卡片
        private void OnConfigCardRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {

        }

        // 备份配置中的所有文件夹
        private async void OnBackupAllFoldersClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                if (config.SourceFolders == null || config.SourceFolders.Count == 0)
                {
                    var resourceLoader = ResourceLoader.GetForViewIndependentUse();
                    var dialog = new ContentDialog
                    {
                        Title = resourceLoader.GetString("HomePage_ContextMenu_NoFolders_Title"),
                        Content = resourceLoader.GetString("HomePage_ContextMenu_NoFolders_Content"),
                        CloseButtonText = resourceLoader.GetString("Common_Ok"),
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                    return;
                }

                foreach (var folder in config.SourceFolders)
                {
                    await BackupService.BackupFolderAsync(config, folder, "HomePage Batch Backup");
                }
            }
        }

        // 打开目标文件夹
        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                if (!string.IsNullOrWhiteSpace(config.DestinationPath))
                {
                    try
                    {
                        if (!Directory.Exists(config.DestinationPath))
                        {
                            Directory.CreateDirectory(config.DestinationPath);
                        }

                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = config.DestinationPath,
                            UseShellExecute = true,
                            Verb = "open"
                        });
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError($"Failed to open destination: {ex.Message}");
                    }
                }
            }
        }

        // 编辑配置（跳转到管理页）
        private void OnEditConfigClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                App.Shell.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));
            }
        }

        // 删除配置
        private async void OnDeleteConfigClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                var resourceLoader = ResourceLoader.GetForViewIndependentUse();

                var dialog = new ContentDialog
                {
                    Title = resourceLoader.GetString("HomePage_ContextMenu_DeleteConfirm_Title"),
                    Content = string.Format(resourceLoader.GetString("HomePage_ContextMenu_DeleteConfirm_Content"), config.Name),
                    PrimaryButtonText = resourceLoader.GetString("HomePage_ContextMenu_DeleteConfirm_Delete"),
                    CloseButtonText = resourceLoader.GetString("HomePage_CancelButton"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    ConfigService.CurrentConfig.BackupConfigs.Remove(config);
                    ConfigService.Save();
                    RefreshConfigsView();
                    RefreshFavorites();
                }
            }
        }

        #endregion
    }
}