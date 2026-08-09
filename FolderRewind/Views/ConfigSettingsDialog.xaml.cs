using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace FolderRewind.Views
{
    public sealed partial class ConfigSettingsDialog : ContentDialog
    {
        private static ConfigSettingsDialog? _instance;

        public static ConfigSettingsDialog Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ConfigSettingsDialog(new BackupConfig
                    {
                        Name = string.Empty,
                        Cloud = new CloudSettings(),
                        BackupScope = new BackupScopeSettings(),
                        Archive = new ArchiveSettings
                        {
                            CompressionLevel = 5,
                            Format = "7z",
                            Method = "LZMA2",
                            KeepCount = 5,
                            Mode = BackupMode.Full
                        },
                        Automation = new AutomationSettings()
                    });
                }
                return _instance;
            }
        }

        public BackupConfig Config { get; private set; }
        public ConfigSettingsDialogViewModel ViewModel { get; }
        private bool _isDialogReady;
        private Microsoft.UI.Xaml.UIElement? _currentTabContent;
        private readonly Dictionary<string, bool> _tabLoaded = new();

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> ConfigTypesView { get; } = new();

        public string SelectedConfigType
        {
            get
            {
                if (Config == null) return "Default";
                return string.IsNullOrWhiteSpace(Config.ConfigType) ? "Default" : Config.ConfigType;
            }
            set
            {
                if (Config == null) return;
                if (string.IsNullOrWhiteSpace(value))
                {
                    // ComboBox 初始化 ItemsSource/SelectedItem 时可能短暂回写 null；这不是用户选择。
                    return;
                }

                if (string.Equals(Config.ConfigType, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Config.ConfigType = value;
                ViewModel?.RefreshBackupScopeOptions();
                if (_isDialogReady)
                {
                    RebuildBackupScopeParameterPanel();
                    Bindings.Update();
                }
            }
        }

        public string ConfigFilePath => ConfigService.ConfigFilePath;

        public int FormatSelectedIndex
        {
            get => Config.Archive.Format == "zip" ? 1 : 0;
            set => Config.Archive.Format = value == 1 ? "zip" : "7z";
        }

        /// <summary>
        /// 压缩算法选择索引
        /// </summary>
        private static readonly string[] CompressionMethods = { "LZMA2", "Deflate", "BZip2", "zstd" };

        /// <summary>
        /// 根据当前压缩算法返回压缩等级的最小值
        /// </summary>
        /// <summary>
        /// 根据当前压缩算法返回压缩等级的最大值
        /// </summary>
        /// <summary>
        /// 获取各压缩算法的有效压缩等级范围
        /// </summary>
        public int MethodSelectedIndex
        {
            get
            {
                var idx = Array.IndexOf(CompressionMethods, Config.Archive.Method);
                return idx >= 0 ? idx : 0; // 默认 LZMA2
            }
            set
            {
                if (value >= 0 && value < CompressionMethods.Length)
                {
                    Config.Archive.Method = CompressionMethods[value];
                }
            }
        }

        /// <summary>
        /// 当压缩算法变更时，更新压缩等级滑块的有效范围，并将当前值限制在新范围内
        /// </summary>
        private void UpdateCompressionLevelSliderRange()
        {
            if (CompressionLevelSlider == null) return;
            var (min, max) = ArchiveCompressionPolicy.GetLevelRange(Config?.Archive?.Method);
            CompressionLevelSlider.Minimum = min;
            CompressionLevelSlider.Maximum = max;
            // 将当前值限制在新的有效范围内
            if (Config?.Archive != null)
            {
                Config.Archive.CompressionLevel = Math.Clamp(Config.Archive.CompressionLevel, min, max);
            }
        }

        public ConfigSettingsDialog(BackupConfig config)
        {
            this.InitializeComponent();

            // Force-load the initially selected tab (General) since x:Load="False" defers its creation
            LoadTabContent("General");

            this.Config = config;
            this.Config.Cloud ??= new CloudSettings();
            this.Config.BackupScope ??= new BackupScopeSettings();
            this.ViewModel = new ConfigSettingsDialogViewModel(this.Config);
            this.XamlRoot = MainWindowService.GetXamlRoot();

            // 应用当前主题到对话框
            ThemeService.ApplyThemeToDialog(this);

            ConfigTypesView.Clear();
            foreach (var t in PluginService.GetAllSupportedConfigTypes())
            {
                if (string.Equals(t, "Encrypted", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ConfigTypesView.Add(t);
            }

            // 确保 "Default" 始终存在（插件系统未启用时列表可能为空）
            if (!ConfigTypesView.OfType<string>().Any(t => string.Equals(t, "Default", StringComparison.OrdinalIgnoreCase)))
            {
                ConfigTypesView.Insert(0, "Default");
            }

            if (string.Equals(Config.ConfigType, "Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                Config.ConfigType = "Default";
            }

            // 如果当前类型不在列表里，也允许展示出来（避免旧配置类型丢失）
            if (!ConfigTypesView.OfType<string>().Any(t => string.Equals(t, Config.ConfigType, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigTypesView.Add(Config.ConfigType);
            }

            IconGrid.ItemsSource = IconCatalog.ConfigIconGlyphs;
            IconGrid.SelectedItem = IconCatalog.ConfigIconGlyphs.FirstOrDefault(i => i == Config.IconGlyph) ?? IconCatalog.ConfigIconGlyphs.First();

            Config.PropertyChanged += OnDialogConfigPropertyChanged;
            Config.Cloud.PropertyChanged += OnDialogCloudPropertyChanged;

            InitializeScheduleUI();
            RebuildBackupScopeParameterPanel();
            UpdateCloudBindings();
            Bindings.Update();
            _isDialogReady = true;
        }

        private void OnSettingsSelectorBarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is not string tag) return;

            var tabName = GetTabNameFromTag(tag);
            if (tabName == null) return;

            // Hide current tab
            if (_currentTabContent != null)
            {
                _currentTabContent.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }

            // Load or find the target tab (FindName triggers x:Load on first call)
            if (!_tabLoaded.ContainsKey(tabName))
            {
                var element = FindName(tabName) as Microsoft.UI.Xaml.UIElement;
                if (element != null)
                {
                    _tabLoaded[tabName] = true;
                }
            }

            var tabContent = FindName(tabName) as Microsoft.UI.Xaml.UIElement;
            if (tabContent != null)
            {
                tabContent.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                _currentTabContent = tabContent;
            }

            // Update ViewModel state
            var selectedIndex = sender.Items.IndexOf(sender.SelectedItem);
            if (selectedIndex >= 0)
            {
                ViewModel.SelectedPageIndex = selectedIndex;
            }

            // Initialize tab-specific deferred content
            switch (tag)
            {
                case "Backup":
                    RebuildBackupScopeParameterPanel();
                    break;
                case "Automation":
                    break;
            }

            UpdateCloudBindings();
        }

        private void LoadTabContent(string tag)
        {
            var tabName = GetTabNameFromTag(tag);
            if (tabName == null) return;

            var element = FindName(tabName) as Microsoft.UI.Xaml.UIElement;
            if (element != null)
            {
                element.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                _currentTabContent = element;
                _tabLoaded[tabName] = true;
            }
        }

        private static string? GetTabNameFromTag(string tag) => tag switch
        {
            "General" => "GeneralTabScrollViewer",
            "Backup" => "BackupTabScrollViewer",
            "Restore" => "RestoreTabScrollViewer",
            "Automation" => "AutomationTabScrollViewer",
            "Cloud" => "CloudTabScrollViewer",
            "Filter" => "FilterTabScrollViewer",
            _ => null
        };

        public void Rebind(BackupConfig config)
        {
            // Unbind old config event handlers
            if (_isDialogReady)
            {
                ViewModel.Unbind();
                Config.PropertyChanged -= OnDialogConfigPropertyChanged;
                Config.Cloud.PropertyChanged -= OnDialogCloudPropertyChanged;
            }

            // Reset config
            Config = config;
            Config.Cloud ??= new CloudSettings();
            Config.BackupScope ??= new BackupScopeSettings();

            // Update ViewModel
            ViewModel.Rebind(config);

            // Reset tab state
            _tabLoaded.Clear();
            _currentTabContent = null;

            // 重置所有 tab ScrollViewer 的可见性，防止重影
            GeneralTabScrollViewer?.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            BackupTabScrollViewer?.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            RestoreTabScrollViewer?.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            AutomationTabScrollViewer?.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            CloudTabScrollViewer?.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            FilterTabScrollViewer?.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            // Guard: same as constructor — convert "Encrypted" to "Default"
            if (string.Equals(Config.ConfigType, "Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                Config.ConfigType = "Default";
            }

            // Reset ConfigTypesView
            ConfigTypesView.Clear();
            foreach (var t in PluginService.GetAllSupportedConfigTypes())
            {
                if (!string.Equals(t, "Encrypted", StringComparison.OrdinalIgnoreCase))
                    ConfigTypesView.Add(t);
            }
            if (!ConfigTypesView.OfType<string>().Any(t => string.Equals(t, "Default", StringComparison.OrdinalIgnoreCase)))
                ConfigTypesView.Insert(0, "Default");
            if (!ConfigTypesView.OfType<string>().Any(t => string.Equals(t, Config.ConfigType, StringComparison.OrdinalIgnoreCase)))
                ConfigTypesView.Add(Config.ConfigType);

            // Reset icon grid
            IconGrid.ItemsSource = IconCatalog.ConfigIconGlyphs;
            IconGrid.SelectedItem = IconCatalog.ConfigIconGlyphs.FirstOrDefault(i => i == Config.IconGlyph) ?? IconCatalog.ConfigIconGlyphs.First();

            // Re-register config events
            Config.PropertyChanged += OnDialogConfigPropertyChanged;
            Config.Cloud.PropertyChanged += OnDialogCloudPropertyChanged;

            // 重新打开时 SelectionChanged 可能不会触发，或者控件仍保留关闭前的选中项；
            // 这里以当前实际选中的页签为准显式恢复内容，避免标签和正文不同步。
            if (_currentTabContent == null)
            {
                var selectedTag = ConfigSelectorBar.SelectedItem?.Tag as string
                    ?? (ConfigSelectorBar.Items.FirstOrDefault() as SelectorBarItem)?.Tag as string;
                if (!string.IsNullOrWhiteSpace(selectedTag))
                {
                    LoadTabContent(selectedTag);

                    var selectedIndex = ConfigSelectorBar.Items.IndexOf(ConfigSelectorBar.SelectedItem);
                    if (selectedIndex >= 0)
                    {
                        ViewModel.SelectedPageIndex = selectedIndex;
                    }
                }
            }

            RebuildBackupScopeParameterPanel();
            UpdateCloudBindings();
            Bindings.Update();
        }

        private void OnDialogConfigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BackupConfig.Name) || e.PropertyName == nameof(BackupConfig.DestinationPath))
            {
                UpdateCloudBindings();
            }
        }

        private void OnDialogCloudPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateCloudBindings();
        }

        private void OnOpenCloudGuideClick(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenCloudGuideWebsite();
        }

        private void UpdateCloudBindings()
        {
            ViewModel.RefreshCloudUi();
            _ = DispatcherQueue.TryEnqueue(() => Bindings.Update());
        }

        private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            foreach (var folder in Config.SourceFolders ?? new ObservableCollection<ManagedFolder>())
            {
                if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                        Config.DestinationPath,
                        folder.DisplayName,
                        folder.Path,
                        out _,
                        out var backupSubDir,
                        out var metadataDir))
                {
                    args.Cancel = true;
                    await ShowValidationErrorAsync(I18n.GetString("BackupService_Log_InvalidStorageFolderName"));
                    return;
                }

                var overlap = BackupPathOverlapPolicy.Validate(folder.Path, backupSubDir, metadataDir);
                if (!overlap.IsSafe)
                {
                    args.Cancel = true;
                    await ShowValidationErrorAsync(I18n.Format(
                        "BackupService_Folder_SourceDestinationOverlap",
                        overlap.SourcePath,
                        overlap.TargetPath));
                    return;
                }
            }

            if (!ViewModel.TryValidateAndNormalizeAdditionalSevenZipArguments(out var errorMessage))
            {
                args.Cancel = true;

                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("ConfigSettingsDialog_Additional7zArgsSaveErrorTitle"),
                    Content = new TextBlock
                    {
                        Text = errorMessage,
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
                return;
            }

            if (!ViewModel.TryValidateFilters(out errorMessage))
            {
                args.Cancel = true;

                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("Common_Failed"),
                    Content = new TextBlock
                    {
                        Text = errorMessage,
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
                return;
            }

            if (!ViewModel.TryValidateBackupScope(out errorMessage))
            {
                args.Cancel = true;

                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("Common_Failed"),
                    Content = new TextBlock
                    {
                        Text = errorMessage,
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
                return;
            }

            ConfigService.Save();
        }

        private async Task ShowValidationErrorAsync(string errorMessage)
        {
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Common_Failed"),
                Content = new TextBlock
                {
                    Text = errorMessage,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnDeleteClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;

            // WinUI同一时间只能打开一个 ContentDialog。
            // 当前设置对话框处于打开状态时，如果直接 ShowAsync 另一个对话框会抛出：
            // "Only a single ContentDialog can be open at any time."
            // 因此这里先隐藏当前对话框，再显示确认对话框；取消再把设置对话框重新显示出来。
            sender.Hide();
            await Task.Yield();

            var confirm = new ContentDialog
            {
                Title = I18n.GetString("ConfigSettingsDialog_DeleteConfirmTitle"),
                Content = new TextBlock { Text = I18n.GetString("ConfigSettingsDialog_DeleteConfirmContent"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = I18n.GetString("Common_Delete"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };

            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                await this.ShowAsync();
                return;
            }

            var current = ConfigService.CurrentConfig;
            if (current?.BackupConfigs == null)
            {
                LogService.Log(I18n.GetString("Config_Delete_CurrentConfigNull"));
                return;
            }

            // 有些页面传入的 Config 可能不是 CurrentConfig.BackupConfigs 中的同一引用
            // 必须按 Id 找到真实对象再删除
            var toRemove = current.BackupConfigs.FirstOrDefault(c => string.Equals(c.Id, Config.Id, StringComparison.OrdinalIgnoreCase));
            if (toRemove == null)
            {
                LogService.Log(I18n.GetString("Config_Delete_NotFound"));
                return;
            }

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var fallback = current.BackupConfigs.FirstOrDefault(c => !string.Equals(c.Id, toRemove.Id, StringComparison.OrdinalIgnoreCase));

            current.BackupConfigs.Remove(toRemove);

            // 清除加密配置的存储密码
            if (toRemove.IsEncrypted)
            {
                EncryptionService.RemovePassword(toRemove.Id);
            }

            if (settings != null)
            {
                if (settings.LastManagerConfigId == Config.Id)
                {
                    settings.LastManagerConfigId = fallback?.Id ?? string.Empty;
                    settings.LastManagerFolderPath = string.Empty;
                }

                if (settings.LastHistoryConfigId == Config.Id)
                {
                    settings.LastHistoryConfigId = fallback?.Id ?? string.Empty;
                    settings.LastHistoryFolderPath = string.Empty;
                }
            }

            ConfigService.Save();
        }

        private static void OpenPathInShell(string path)
        {
            if (!ShellPathService.TryOpenPath(path, out var error))
            {
                LogService.Log(I18n.Format("Config_OpenPath_Failed", error ?? string.Empty));
            }
        }
    }
}
