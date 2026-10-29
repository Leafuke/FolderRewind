using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class PluginStorePage : Page
    {
        public PluginStorePageViewModel ViewModel { get; } = new();

        public PluginStorePage()
        {
            this.InitializeComponent();
        }

        private void OnAssetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Grid grid || grid.Children.OfType<StackPanel>().FirstOrDefault(p => p.Tag as string == "PluginActions") is not { } actions) return;
            var narrow = e.NewSize.Width < 680 || actions.DesiredSize.Width > e.NewSize.Width * 0.55;
            Grid.SetRow(actions, narrow ? 1 : 0);
            Grid.SetColumn(actions, narrow ? 0 : 1);
            Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : GridLength.Auto;
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

        private async void OnEnableClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not PluginStoreAssetItem item)
            {
                return;
            }

            await ViewModel.EnableCommand.ExecuteAsync(item);
        }
    }
}
