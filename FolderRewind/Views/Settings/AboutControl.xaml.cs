using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FolderRewind.Views.Settings
{
    public sealed partial class AboutControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public AboutControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        private void OnJoinGroupButtonClick(object sender, RoutedEventArgs e)
            => FlyoutBase.ShowAttachedFlyout(sender as FrameworkElement);
    }
}
