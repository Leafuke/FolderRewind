using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FolderRewind.Views
{
    public sealed partial class PluginStorePage : Page
    {
        public PluginStorePageViewModel ViewModel { get; } = new();

        public PluginStorePage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.ActivateAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Deactivate();
        }

        private async void OnInstallClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not PluginStoreAssetItem item)
            {
                return;
            }

            await ViewModel.InstallCommand.ExecuteAsync(item);
        }
    }
}
