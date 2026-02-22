using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class ConfigSettingsDialog : ContentDialog
    {
        public BackupConfig Config { get; private set; }

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> ConfigTypesView { get; } = new();

        public string SelectedConfigType
        {
            get => Config?.ConfigType ?? "Default";
            set
            {
                if (Config == null) return;
                Config.ConfigType = string.IsNullOrWhiteSpace(value) ? "Default" : value;
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
        public int CompressionLevelMin => GetCompressionLevelRange(Config?.Archive?.Method).Min;

        /// <summary>
        /// 根据当前压缩算法返回压缩等级的最大值
        /// </summary>
        public int CompressionLevelMax => GetCompressionLevelRange(Config?.Archive?.Method).Max;

        /// <summary>
        /// 获取各压缩算法的有效压缩等级范围
        /// </summary>
        private static (int Min, int Max) GetCompressionLevelRange(string method)
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
                    UpdateCompressionLevelSliderRange();
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

        private void OnMethodSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCompressionLevelSliderRange();
        }

        public ConfigSettingsDialog(BackupConfig config)
        {
            this.InitializeComponent();
            this.Config = config;
            this.XamlRoot = App._window.Content.XamlRoot;

            // 应用当前主题到对话框
            ThemeService.ApplyThemeToDialog(this);

            PluginService.Initialize();
            ConfigTypesView.Clear();
            foreach (var t in PluginService.GetAllSupportedConfigTypes())
            {
                ConfigTypesView.Add(t);
            }

            // 如果当前类型不在列表里，也允许展示出来（避免旧配置类型丢失）
            if (!ConfigTypesView.OfType<string>().Any(t => string.Equals(t, Config.ConfigType, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigTypesView.Add(Config.ConfigType);
            }

            IconGrid.ItemsSource = IconCatalog.ConfigIconGlyphs;
            IconGrid.SelectedItem = IconCatalog.ConfigIconGlyphs.FirstOrDefault(i => i == Config.IconGlyph) ?? IconCatalog.ConfigIconGlyphs.First();

            InitializeScheduleUI();
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

            // Set header texts
            ScheduleEntriesHeader.Text = I18n.GetString("Schedule_Header");
            ScheduleEntriesDesc.Text = I18n.GetString("Schedule_Description");
            AddScheduleText.Text = I18n.GetString("Schedule_Add");

            // Build existing entries
            RebuildScheduleEntriesUI();
        }

        private void RebuildScheduleEntriesUI()
        {
            ScheduleEntriesPanel.Children.Clear();
            if (Config.Automation.ScheduleEntries == null) return;

            for (int idx = 0; idx < Config.Automation.ScheduleEntries.Count; idx++)
            {
                var entry = Config.Automation.ScheduleEntries[idx];
                ScheduleEntriesPanel.Children.Add(BuildScheduleEntryRow(entry, idx));
            }
        }

        private UIElement BuildScheduleEntryRow(ScheduleEntry entry, int index)
        {
            var root = new StackPanel { Spacing = 4 };

            // Row 1: Month Day Hour Minute + Delete button
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            // Month ComboBox
            var monthLabel = new TextBlock
            {
                Text = I18n.GetString("Schedule_Month"),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            };
            Grid.SetColumn(monthLabel, 0);
            row.Children.Add(monthLabel);

            var monthBox = new ComboBox
            {
                ItemsSource = _monthOptions,
                SelectedIndex = Math.Clamp(entry.MonthSelection, 0, 12),
                MinWidth = 72,
                Tag = entry
            };
            monthBox.SelectionChanged += OnMonthSelectionChanged;
            // Disable month when day is "every"
            monthBox.IsEnabled = entry.DaySelection != 0;
            Grid.SetColumn(monthBox, 1);
            row.Children.Add(monthBox);

            // Day ComboBox
            var dayLabel = new TextBlock
            {
                Text = I18n.GetString("Schedule_Day"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            };
            Grid.SetColumn(dayLabel, 2);
            row.Children.Add(dayLabel);

            var dayBox = new ComboBox
            {
                ItemsSource = _dayOptions,
                SelectedIndex = Math.Clamp(entry.DaySelection, 0, 31),
                MinWidth = 72,
                Tag = entry
            };
            dayBox.SelectionChanged += OnDaySelectionChanged;
            Grid.SetColumn(dayBox, 3);
            row.Children.Add(dayBox);

            // Hour:Minute
            var timePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 0, 0, 0) };
            var hourBox = new NumberBox
            {
                Value = entry.Hour,
                Minimum = 0,
                Maximum = 23,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                MinWidth = 70,
                Tag = entry
            };
            hourBox.ValueChanged += OnHourValueChanged;
            var colonText = new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var minuteBox = new NumberBox
            {
                Value = entry.Minute,
                Minimum = 0,
                Maximum = 59,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                MinWidth = 70,
                Tag = entry
            };
            minuteBox.ValueChanged += OnMinuteValueChanged;
            timePanel.Children.Add(hourBox);
            timePanel.Children.Add(colonText);
            timePanel.Children.Add(minuteBox);
            Grid.SetColumn(timePanel, 4);
            row.Children.Add(timePanel);

            // Delete button
            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                Tag = entry,
                Padding = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Center
            };
            deleteBtn.Click += OnRemoveScheduleEntryClick;
            Grid.SetColumn(deleteBtn, 6);
            row.Children.Add(deleteBtn);

            root.Children.Add(row);

            // Row 2: Next run display
            var nextRunText = new TextBlock
            {
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(2, 0, 0, 0)
            };
            UpdateNextRunText(nextRunText, entry);
            nextRunText.Tag = entry;

            // Listen for entry property changes to update next run
            entry.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ScheduleEntry.NextRunDisplay))
                {
                    DispatcherQueue.TryEnqueue(() => UpdateNextRunText(nextRunText, entry));
                }
            };

            root.Children.Add(nextRunText);

            // Separator
            root.Children.Add(new Border
            {
                Height = 1,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                Margin = new Thickness(0, 2, 0, 0)
            });

            return root;
        }

        private void UpdateNextRunText(TextBlock textBlock, ScheduleEntry entry)
        {
            textBlock.Text = I18n.Format("Schedule_NextRun", entry.NextRunDisplay);
        }

        private void OnMonthSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox box && box.Tag is ScheduleEntry entry)
            {
                entry.MonthSelection = box.SelectedIndex;
                // Rebuild to update month enable state
                RebuildScheduleEntriesUI();
            }
        }

        private void OnDaySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox box && box.Tag is ScheduleEntry entry)
            {
                entry.DaySelection = box.SelectedIndex;
                // Rebuild to update month enable state
                RebuildScheduleEntriesUI();
            }
        }

        private void OnHourValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (sender.Tag is ScheduleEntry entry && !double.IsNaN(args.NewValue))
            {
                entry.Hour = (int)args.NewValue;
            }
        }

        private void OnMinuteValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (sender.Tag is ScheduleEntry entry && !double.IsNaN(args.NewValue))
            {
                entry.Minute = (int)args.NewValue;
            }
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
            RebuildScheduleEntriesUI();
        }

        private void OnRemoveScheduleEntryClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ScheduleEntry entry)
            {
                Config.Automation.ScheduleEntries?.Remove(entry);
                RebuildScheduleEntriesUI();
            }
        }

        public int ModeSelectedIndex
        {
            get => (int)Config.Archive.Mode;
            set => Config.Archive.Mode = (BackupMode)value;
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                Config.DestinationPath = folder.Path;
                DestPathBox.Text = folder.Path;
            }
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
                XamlRoot = App._window?.Content?.XamlRoot ?? this.XamlRoot
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

            if (settings != null)
            {
                if (settings.LastManagerConfigId == Config.Id)
                {
                    settings.LastManagerConfigId = fallback?.Id;
                    settings.LastManagerFolderPath = null;
                }

                if (settings.LastHistoryConfigId == Config.Id)
                {
                    settings.LastHistoryConfigId = fallback?.Id;
                    settings.LastHistoryFolderPath = null;
                }
            }

            ConfigService.Save();
        }

        private static void OpenPathInShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Config_OpenPath_Failed", ex.Message));
            }
        }
        private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BlacklistBox.Text))
            {
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

        // --- 还原白名单 ---
        private void OnAddRestoreWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(RestoreWhitelistBox.Text))
            {
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