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
            => ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.AddSchedule));

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
                ViewModel.EditCommand.Execute(new ConfigSettingsEditRequest(ConfigSettingsEdit.RemoveSchedule, entry));
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

        public bool IsRollingModeSelected => Config?.Archive?.Mode == BackupMode.Rolling;

        public string RollingModeDescriptionText => I18n.GetString("ConfigSettingsDialog_RollingDescription");

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
                    case PluginFormFieldType.Boolean:
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
                    case PluginFormFieldType.Integer:
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
                    case PluginFormFieldType.MultilineString:
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

    }
}
