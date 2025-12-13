using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class ConfigSettingsDialog : ContentDialog
    {
        public BackupConfig Config { get; private set; }

        // 辅助属性：将 ComboBox 索引映射到 Config.Archive.Format 字符串
        public int FormatSelectedIndex
        {
            get => Config.Archive.Format == "zip" ? 1 : 0;
            set => Config.Archive.Format = value == 1 ? "zip" : "7z";
        }

        public ConfigSettingsDialog(BackupConfig config)
        {
            this.InitializeComponent();
            this.Config = config;
            // 确保 XamlRoot 被设置，否则 ContentDialog 在 WinUI3 会崩
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

            // 获取窗口句柄 (确保 App.Window 存在)
            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                Config.DestinationPath = folder.Path;
                // 强制更新 UI (因为 TextBox 绑定是 TwoWay，但从代码改需要通知)
                // 这里简单重新赋值一下 DataContext 或者利用 PropertyChanged
                DestPathBox.Text = folder.Path;
            }
        }
        // 添加黑名单
        private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BlacklistBox.Text))
            {
                Config.Filters.Blacklist.Add(BlacklistBox.Text.Trim());
                BlacklistBox.Text = "";
            }
        }

        // 移除黑名单 (这需要一点技巧，因为 ListView 绑定的是 strings)
        private void OnRemoveBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.Blacklist.Remove(item);
            }
        }
    }
}