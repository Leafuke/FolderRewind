using FolderRewind.Models;
using FolderRewind.History.Application;
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
        private bool _subscriptionsAttached = true;
        private bool _showingChild;
        private Microsoft.UI.Xaml.UIElement? _currentTabContent;
        private readonly Dictionary<string, bool> _tabLoaded = new();

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> ConfigKindsView { get; } = new();
        private PluginConfigKindOption? _selectedConfigKind;

        public PluginConfigKindOption? SelectedConfigKind
        {
            get => _selectedConfigKind;
            set
            {
                if (Config == null || value == null) return;

                if (string.Equals(_selectedConfigKind?.StableKey, value.StableKey, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedConfigKind = value;
                // 配置类型名称只用于显示；实际持久化的是稳定 Config Kind 身份。
                ViewModel?.SelectConfigKind(value);
                if (ConfigKindDescriptionText != null) ConfigKindDescriptionText.Text = value.Description;
                ViewModel?.RefreshBackupScopeOptions();
                if (_isDialogReady)
                {
                    RebuildBackupScopeParameterPanel();
                    Bindings.Update();
                }
            }
        }

        public string ConfigFilePath => ViewModel.ConfigFilePath;

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
            this.ViewModel = new ConfigSettingsDialogViewModel(this.Config,
                viewModel => new ConfigSettingsActions(viewModel, () => XamlRoot));
            Opened += OnDialogOpened;
            Closed += OnDialogClosed;
            this.XamlRoot = MainWindowService.GetXamlRoot();

            // 应用当前主题到对话框
            ThemeService.ApplyThemeToDialog(this);

            RefreshConfigKindOptions();

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

            RefreshConfigKindOptions();

            // Reset icon grid
            IconGrid.ItemsSource = IconCatalog.ConfigIconGlyphs;
            IconGrid.SelectedItem = IconCatalog.ConfigIconGlyphs.FirstOrDefault(i => i == Config.IconGlyph) ?? IconCatalog.ConfigIconGlyphs.First();

            // Re-register config events
            Config.PropertyChanged += OnDialogConfigPropertyChanged;
            Config.Cloud.PropertyChanged += OnDialogCloudPropertyChanged;
            _subscriptionsAttached = true;

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

        private void RefreshConfigKindOptions()
        {
            ConfigKindsView.Clear();
            var options = ViewModel.GetConfigKindOptions();
            foreach (var option in options) ConfigKindsView.Add(option);

            var selected = ViewModel.ResolveConfigKind();
            var matching = options.FirstOrDefault(option =>
                string.Equals(option.StableKey, selected.StableKey, StringComparison.OrdinalIgnoreCase));
            if (matching is null)
            {
                ConfigKindsView.Add(selected);
                matching = selected;
            }
            _selectedConfigKind = matching;
            if (ConfigKindDescriptionText != null) ConfigKindDescriptionText.Text = matching.Description;
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
            var deferral = args.GetDeferral();
            args.Cancel = true;
            try
            {
                if (!ViewModel.SaveCommand.CanExecute(null)) return;
                IsEnabled = false;
                await ViewModel.SaveCommand.ExecuteAsync(null);
                args.Cancel = !ViewModel.LastSaveSucceeded;
            }
            finally { IsEnabled = true; deferral.Complete(); }
        }



        private void OnDeleteClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            if (!ViewModel.DeleteCommand.CanExecute(null) || _showingChild) return;
            _showingChild = true;
            sender.Hide();
            TaskObserver.Observe(DeleteAndRestoreAsync(), nameof(ConfigSettingsDialog));
        }

        private async Task DeleteAndRestoreAsync()
        {
            await Task.Yield();
            try
            {
                await ViewModel.DeleteCommand.ExecuteAsync(null);
                if (!ViewModel.LastDeleteSucceeded)
                    await AppDialogService.Default.ShowCustomAsync(this, XamlRoot);
            }
            finally { _showingChild = false; }
        }

        private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            ViewModel.ActivateActions();
            if (_subscriptionsAttached) return;
            ViewModel.Rebind(Config);
            ViewModel.SelectedPageIndex = Math.Max(0, ConfigSelectorBar.Items.IndexOf(ConfigSelectorBar.SelectedItem));
            Config.PropertyChanged += OnDialogConfigPropertyChanged;
            Config.Cloud.PropertyChanged += OnDialogCloudPropertyChanged;
            _subscriptionsAttached = true;
            Bindings.Update();
        }

        private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            ViewModel.Unbind();
            Config.PropertyChanged -= OnDialogConfigPropertyChanged;
            Config.Cloud.PropertyChanged -= OnDialogCloudPropertyChanged;
            _subscriptionsAttached = false;
            if (!_showingChild) ViewModel.CancelActions();
        }


    }
}
