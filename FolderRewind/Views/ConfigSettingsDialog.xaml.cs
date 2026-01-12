using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
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

        public string[] IconOptions { get; } = { "\uE8B7", "\uEB9F", "\uE82D", "\uE943", "\uE77B", "\uEA86" };

        // �������ԣ��� ComboBox ����ӳ�䵽 Config.Archive.Format �ַ���
        public int FormatSelectedIndex
        {
            get => Config.Archive.Format == "zip" ? 1 : 0;
            set => Config.Archive.Format = value == 1 ? "zip" : "7z";
        }

        public ConfigSettingsDialog(BackupConfig config)
        {
            this.InitializeComponent();
            this.Config = config;
            // ȷ�� XamlRoot �����ã����� ContentDialog �� WinUI3 ���
            this.XamlRoot = App._window.Content.XamlRoot;

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

            IconGrid.ItemsSource = IconOptions;
            IconGrid.SelectedItem = IconOptions.FirstOrDefault(i => i == Config.IconGlyph) ?? IconOptions.First();
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

            // ��ȡ���ھ�� (ȷ�� App.Window ����)
            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                Config.DestinationPath = folder.Path;
                // ǿ�Ƹ��� UI (��Ϊ TextBox ���� TwoWay�����Ӵ������Ҫ֪ͨ)
                // ��������¸�ֵһ�� DataContext �������� PropertyChanged
                DestPathBox.Text = folder.Path;
            }
        }

        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Config?.DestinationPath))
            {
                LogService.Log("[Config] 目标路径为空，无法打开。");
                return;
            }

            if (!Directory.Exists(Config.DestinationPath))
            {
                LogService.Log("[Config] 目标路径不存在，无法打开。");
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

            var confirm = new ContentDialog
            {
                Title = "删除配置",
                Content = new TextBlock { Text = "确定要删除当前配置吗？此操作不可撤销。", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var fallback = ConfigService.CurrentConfig?.BackupConfigs?.FirstOrDefault(c => !ReferenceEquals(c, Config));

            ConfigService.CurrentConfig.BackupConfigs.Remove(Config);

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
            sender.Hide();
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
                LogService.Log($"[Config] 无法打开路径：{ex.Message}");
            }
        }
        // ���Ӻ�����
        private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BlacklistBox.Text))
            {
                Config.Filters.Blacklist.Add(BlacklistBox.Text.Trim());
                BlacklistBox.Text = "";
            }
        }

        // �Ƴ������� (����Ҫһ�㼼�ɣ���Ϊ ListView �󶨵��� strings)
        private void OnRemoveBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.Blacklist.Remove(item);
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
    }
}