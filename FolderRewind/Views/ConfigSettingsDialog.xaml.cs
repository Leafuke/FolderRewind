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

        public string[] IconOptions { get; } = { "\uE8B7", "\uEB9F", "\uE82D", "\uE943", "\uE77B", "\uEA86" };

        public int FormatSelectedIndex
        {
            get => Config.Archive.Format == "zip" ? 1 : 0;
            set => Config.Archive.Format = value == 1 ? "zip" : "7z";
        }

        public ConfigSettingsDialog(BackupConfig config)
        {
            this.InitializeComponent();
            this.Config = config;
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