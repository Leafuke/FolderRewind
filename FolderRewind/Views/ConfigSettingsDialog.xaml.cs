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

        private static (int Min, int Max) GetCompressionLevelRange(string? method)
        {
            return method switch
            {
                "zstd" => (1, 22),
                "BZip2" => (1, 9),
                "LZMA2" => (0, 9),
                "Deflate" => (0, 9),
                _ => (0, 9),
            };
        }

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
            var (min, max) = GetCompressionLevelRange(Config?.Archive?.Method);
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

        private readonly List<string> _monthOptions = new();
        private readonly List<string> _dayOptions = new();

        private void InitializeScheduleUI()
        {
            // Build month options: [Every, 1, 2, ... 12]
            _monthOptions.Clear();
            _monthOptions.Add(I18n.GetString("Schedule_Every"));
            for (int i = 1; i <= 12; i++) _monthOptions.Add(i.ToString());

            // Build day options: [Every, 1, 2, ... 31]
            _dayOptions.Clear();
            _dayOptions.Add(I18n.GetString("Schedule_Every"));
            for (int i = 1; i <= 31; i++) _dayOptions.Add(i.ToString());

            // Set header texts (guard against x:Load deferred elements)
            if (ScheduleEntriesHeader != null)
                ScheduleEntriesHeader.Text = I18n.GetString("Schedule_Header");
            if (ScheduleEntriesDesc != null)
                ScheduleEntriesDesc.Text = I18n.GetString("Schedule_Description");
            if (AddScheduleText != null)
                AddScheduleText.Text = I18n.GetString("Schedule_Add");
        }


        private void OnAddScheduleEntryClick(object sender, RoutedEventArgs e)
        {
            if (Config.Automation.ScheduleEntries == null)
                Config.Automation.ScheduleEntries = new ObservableCollection<ScheduleEntry>();

            Config.Automation.ScheduleEntries.Add(new ScheduleEntry
            {
                MonthSelection = 0,
                DaySelection = 0,
                Hour = 8,
                Minute = 0
            });
        }

        private void OnScheduleMonthComboLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox cb && cb.ItemsSource == null)
            {
                cb.ItemsSource = _monthOptions;
            }
        }

        private void OnScheduleDayComboLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox cb && cb.ItemsSource == null)
            {
                cb.ItemsSource = _dayOptions;
            }
        }

        private void OnDeleteScheduleEntryClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ScheduleEntry entry)
            {
                Config.Automation.ScheduleEntries?.Remove(entry);
            }
        }

        public int ModeSelectedIndex
        {
            get => (int)Config.Archive.Mode;
            set
            {
                Config.Archive.Mode = (BackupMode)value;
                Bindings.Update();
            }
        }

        public bool IsOverwriteModeSelected => Config?.Archive?.Mode == BackupMode.Overwrite;

        public string OverwriteModeWarningText => I18n.GetString("ConfigSettingsDialog_OverwriteWarning");

        private void OnBackupScopeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isDialogReady)
            {
                return;
            }

            if (sender is ComboBox box)
            {
                ViewModel.SelectBackupScopeByIndex(box.SelectedIndex);
            }

            RebuildBackupScopeParameterPanel();
        }

        private void RebuildBackupScopeParameterPanel()
        {
            if (BackupScopeParametersPanel == null)
            {
                return;
            }

            BackupScopeParametersPanel.Children.Clear();

            foreach (var definition in ViewModel.SelectedBackupScopeParameters)
            {
                if (string.IsNullOrWhiteSpace(definition.Key))
                {
                    continue;
                }

                var container = new StackPanel { Spacing = 4 };

                switch (definition.Type)
                {
                    case PluginSettingType.Boolean:
                    {
                        var checkbox = new CheckBox
                        {
                            Content = definition.DisplayName,
                            IsChecked = ParseBool(ViewModel.GetBackupScopeParameterValue(definition.Key), definition.DefaultValue),
                            Tag = definition.Key
                        };
                        checkbox.Checked += OnBackupScopeBooleanChanged;
                        checkbox.Unchecked += OnBackupScopeBooleanChanged;
                        container.Children.Add(checkbox);
                        break;
                    }
                    case PluginSettingType.Integer:
                    {
                        container.Children.Add(BuildScopeParameterLabel(definition.DisplayName));
                        var box = new NumberBox
                        {
                            Value = double.TryParse(ViewModel.GetBackupScopeParameterValue(definition.Key), out var value) ? value : 0,
                            Tag = definition.Key
                        };
                        box.ValueChanged += OnBackupScopeNumberChanged;
                        container.Children.Add(box);
                        break;
                    }
                    case PluginSettingType.MultilineString:
                    {
                        container.Children.Add(BuildScopeParameterLabel(definition.DisplayName));
                        var initialText = NormalizeMultilineForEditor(ViewModel.GetBackupScopeParameterValue(definition.Key));
                        var box = new TextBox
                        {
                            Text = initialText,
                            AcceptsReturn = true,
                            TextWrapping = TextWrapping.NoWrap,
                            Height = 140,
                            MinHeight = 120,
                            MaxHeight = 260,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Tag = definition.Key
                        };
                        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                        box.Loaded += (_, _) =>
                        {
                            // 动态生成的 TextBox 在模板加载前写入多行文本时，偶尔只按首行完成可视布局。
                            // Loaded 后再同步一次，保证 \n/\r\n 都能以多行形式稳定回显。
                            var loadedText = NormalizeMultilineForEditor(ViewModel.GetBackupScopeParameterValue(definition.Key));
                            if (!string.Equals(box.Text, loadedText, StringComparison.Ordinal))
                            {
                                box.Text = loadedText;
                            }
                        };
                        box.KeyDown += OnBackupScopeMultilineTextBoxKeyDown;
                        box.TextChanged += OnBackupScopeTextChanged;
                        container.Children.Add(box);
                        break;
                    }
                    default:
                    {
                        container.Children.Add(BuildScopeParameterLabel(definition.DisplayName));
                        var box = new TextBox
                        {
                            Text = ViewModel.GetBackupScopeParameterValue(definition.Key),
                            Tag = definition.Key
                        };
                        box.TextChanged += OnBackupScopeTextChanged;
                        container.Children.Add(box);
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(definition.Description))
                {
                    container.Children.Add(new TextBlock
                    {
                        Text = definition.Description,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                }

                BackupScopeParametersPanel.Children.Add(container);
            }
        }

        private static TextBlock BuildScopeParameterLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
        }

        private void OnBackupScopeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox box && box.Tag is string key)
            {
                var value = box.AcceptsReturn
                    ? NormalizeMultilineForStorage(box.Text)
                    : box.Text;
                ViewModel.SetBackupScopeParameterValue(key, value);
            }
        }

        private void OnBackupScopeMultilineTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (sender is not TextBox box || e.Key != VirtualKey.Enter)
            {
                return;
            }

            // ContentDialog 有默认按钮时，Enter 容易被当作“保存”。多行参数框里 Enter 应该稳定插入新行。
            var text = box.Text ?? string.Empty;
            var start = Math.Clamp(box.SelectionStart, 0, text.Length);
            var length = Math.Clamp(box.SelectionLength, 0, text.Length - start);
            var newText = text.Remove(start, length).Insert(start, Environment.NewLine);
            box.Text = newText;
            box.SelectionStart = start + Environment.NewLine.Length;
            e.Handled = true;
        }

        private static string NormalizeMultilineForEditor(string value)
        {
            return NormalizeLineEndings(value, Environment.NewLine);
        }

        private static string NormalizeMultilineForStorage(string value)
        {
            // 配置文件里用 \n 作为稳定格式；读取时仍兼容 WinUI 产生的裸 \r。
            return NormalizeLineEndings(value, "\n");
        }

        private static string NormalizeLineEndings(string? value, string newline)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", newline, StringComparison.Ordinal);
        }

        private void OnBackupScopeBooleanChanged(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box && box.Tag is string key)
            {
                ViewModel.SetBackupScopeParameterValue(key, box.IsChecked == true ? "true" : "false");
            }
        }

        private void OnBackupScopeNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (sender.Tag is string key)
            {
                ViewModel.SetBackupScopeParameterValue(key, double.IsNaN(args.NewValue) ? string.Empty : ((int)args.NewValue).ToString());
            }
        }

        private static bool ParseBool(string value, string? defaultValue)
        {
            var text = string.IsNullOrWhiteSpace(value) ? defaultValue : value;
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase);
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var folderPath = await MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.ConfigSettings.Destination",
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            Config.DestinationPath = folderPath;
            DestPathBox.Text = folderPath;
        }

        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Config?.DestinationPath))
            {
                LogService.Log(I18n.GetString("Config_OpenDestination_Empty"));
                return;
            }

            if (!Directory.Exists(Config.DestinationPath))
            {
                LogService.Log(I18n.GetString("Config_OpenDestination_NotFound"));
                return;
            }

            OpenPathInShell(Config.DestinationPath);
        }

        private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
        {
            ConfigService.OpenConfigFolder();
        }

        private void OnOpenConfigFileClick(object sender, RoutedEventArgs e)
        {
            ConfigService.OpenConfigFile();
        }

        private async void OnSaveAsTemplateClick(object sender, RoutedEventArgs e)
        {
            if (Config == null)
            {
                return;
            }

            // WinUI 同时只允许一个 ContentDialog，先临时隐藏当前设置对话框。
            this.Hide();
            await Task.Yield();

            var templateNameBox = new TextBox
            {
                Header = I18n.GetString("Template_SaveDialog_Name"),
                Text = string.IsNullOrWhiteSpace(Config.Name) ? I18n.GetString("Template_DefaultName") : Config.Name
            };
            var authorBox = new TextBox
            {
                Header = I18n.GetString("Template_SaveDialog_Author"),
                PlaceholderText = I18n.GetString("Template_SaveDialog_AuthorPlaceholder")
            };
            var descriptionBox = new TextBox
            {
                Header = I18n.GetString("Template_SaveDialog_Description"),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 96,
                MaxHeight = 200
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = I18n.GetString("Template_SaveDialog_Hint"),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(templateNameBox);
            panel.Children.Add(authorBox);
            panel.Children.Add(descriptionBox);

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Template_SaveDialog_Title"),
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Save"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var createResult = TemplateService.UpsertTemplateFromConfig(
                    Config,
                    templateNameBox.Text,
                    authorBox.Text,
                    descriptionBox.Text);

                var tipDialog = new ContentDialog
                {
                    Content = createResult.Message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(tipDialog);
                await tipDialog.ShowAsync();
            }

            await this.ShowAsync();
        }

        private void OnApplyCloudTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyCloudTemplate();
            UpdateCloudBindings();
        }

        private async void OnBrowseCloudExecutableClick(object sender, RoutedEventArgs e)
        {
            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.ConfigSettings.CloudExecutable",
                new[] { ".exe", ".cmd", ".bat", ".ps1" },
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            ViewModel.CloudExecutablePathText = filePath;
            UpdateCloudBindings();
        }

        private async void OnBrowseCloudWorkingDirectoryClick(object sender, RoutedEventArgs e)
        {
            var folderPath = await MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.ConfigSettings.CloudWorkingDirectory",
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            ViewModel.CloudWorkingDirectoryText = folderPath;
            UpdateCloudBindings();
        }

        private async void OnOpenCloudSyncClick(object sender, RoutedEventArgs e)
        {
            this.Hide();
            await Task.Yield();

            var dialog = new ConfigCloudSyncDialog(Config)
            {
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };

            await TemplateDialogCoordinatorService.ShowAsync(dialog, this.XamlRoot);
            ViewModel.RefreshCloudUi();
            await this.ShowAsync();
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

        private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ConfigService.Save();
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
        private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BlacklistBox.Text))
            {
                Config.Filters ??= new FilterSettings();
                Config.Filters.Blacklist ??= new ObservableCollection<string>();
                Config.Filters.Blacklist.Add(BlacklistBox.Text.Trim());
                BlacklistBox.Text = "";
            }
        }

        private void OnRemoveBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.Blacklist.Remove(item);
            }
        }

        private void OnAddBackupWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BackupWhitelistBox.Text))
            {
                Config.Filters ??= new FilterSettings();
                Config.Filters.BackupWhitelist ??= new ObservableCollection<string>();
                Config.Filters.BackupWhitelist.Add(BackupWhitelistBox.Text.Trim());
                BackupWhitelistBox.Text = "";
            }
        }

        private void OnRemoveBackupWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.BackupWhitelist.Remove(item);
            }
        }

        // --- 还原白名单 ---
        private void OnAddRestoreWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(RestoreWhitelistBox.Text))
            {
                Config.Filters ??= new FilterSettings();
                Config.Filters.RestoreWhitelist ??= new ObservableCollection<string>();
                Config.Filters.RestoreWhitelist.Add(RestoreWhitelistBox.Text.Trim());
                RestoreWhitelistBox.Text = "";
            }
        }

        private void OnRemoveRestoreWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.RestoreWhitelist.Remove(item);
            }
        }

        private void OnIconSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IconGrid.SelectedItem is string glyph && !string.IsNullOrWhiteSpace(glyph))
            {
                Config.IconGlyph = glyph;
                ConfigService.Save();
            }
        }

        // --- 自定义文件类型处理规则 ---
        private void OnAddFileTypeRuleClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(FileTypePatternBox.Text))
            {
                var level = (int)FileTypeLevelBox.Value;
                if (double.IsNaN(FileTypeLevelBox.Value)) level = 1;
                level = Math.Clamp(level, 0, 9);

                Config.Archive.FileTypeRules.Add(new FileTypeRule
                {
                    Pattern = FileTypePatternBox.Text.Trim(),
                    CompressionLevel = level
                });
                FileTypePatternBox.Text = "";
                FileTypeLevelBox.Value = 1;
            }
        }

        private void OnRemoveFileTypeRuleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FileTypeRule rule)
            {
                Config.Archive.FileTypeRules.Remove(rule);
            }
        }
    }
}
