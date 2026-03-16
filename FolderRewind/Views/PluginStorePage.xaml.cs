using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;

namespace FolderRewind.Views
{
    public sealed partial class PluginStorePage : Page
    {
        public PluginStorePageViewModel ViewModel { get; } = new();

        public PluginStorePage()
        {
            this.InitializeComponent();
            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            await ViewModel.EnsureInitialLoadAsync();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelPendingOperations();
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
