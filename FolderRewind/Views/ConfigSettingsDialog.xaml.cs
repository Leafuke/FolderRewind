using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class ConfigSettingsDialog : ContentDialog
    {
        public BackupConfig Config { get; private set; }

        public string ConfigFilePath => ConfigService.ConfigFilePath;

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
    }
}