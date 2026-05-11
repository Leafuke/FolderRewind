using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace FolderRewind.Views
{
    public sealed partial class ConfigurationPage : Page
    {
        public BackupConfig? CurrentConfig { get; set; }

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
            }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            if (CurrentConfig == null) return;

            try
            {
                var dialog = new ConfigSettingsDialog(CurrentConfig);
                dialog.XamlRoot = this.XamlRoot;
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("ConfigSettingsDialog_OpenFailed_Log", ex.Message), nameof(ConfigurationPage), ex);
                NotificationService.ShowError(
                    I18n.Format("ConfigSettingsDialog_OpenFailed", ex.Message),
                    I18n.GetString("ConfigSettingsDialog_OpenFailed_Title"));
            }
        }
    }
}
