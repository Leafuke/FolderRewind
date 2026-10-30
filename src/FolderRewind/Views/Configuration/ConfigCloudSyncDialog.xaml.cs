using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;

namespace FolderRewind.Views
{
    public sealed partial class ConfigCloudSyncDialog : ContentDialog
    {
        public ConfigCloudSyncDialogViewModel ViewModel { get; }

        public ConfigCloudSyncDialog(BackupConfig config)
        {
            InitializeComponent();
            ViewModel = new ConfigCloudSyncDialogViewModel(config);
            XamlRoot = MainWindowService.GetXamlRoot();
            ThemeService.ApplyThemeToDialog(this);
            Bindings.Update();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await ViewModel.InitializeAsync();
        }

        private async void OnRefreshAnalysisClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshAnalysisAsync();
        }

        private async void OnDownloadSyncClick(object sender, RoutedEventArgs e)
        {
            bool shouldClose = await ViewModel.ExecuteSyncAsync().ConfigureAwait(true);
            if (shouldClose)
            {
                Hide();
            }
        }

        private async void OnUploadHistoryClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.UploadHistoryAsync().ConfigureAwait(true);
        }
    }
}
