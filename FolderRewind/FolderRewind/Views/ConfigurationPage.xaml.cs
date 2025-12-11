using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace FolderRewind.Views
{
    public sealed partial class ConfigurationPage : Page
    {
        public BackupConfig CurrentConfig { get; set; }

        public ConfigurationPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is BackupConfig config)
            {
                CurrentConfig = config;

                // 模拟一些数据用于展示
                if (CurrentConfig.Folders.Count == 0)
                {
                    CurrentConfig.Folders.Add(new ManagedFolder { DisplayName = "我的文档", FullPath = @"C:\Docs", LastBackupTime = "10:00" });
                    CurrentConfig.Folders.Add(new ManagedFolder { DisplayName = "源代码", FullPath = @"D:\Git", LastBackupTime = "昨天" });
                }
            }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ConfigurationSettingsDialog();
            dialog.XamlRoot = this.XamlRoot;
            await dialog.ShowAsync();
        }
    }
}