using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings
{
    public sealed partial class PresetSettingsControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public PresetSettingsControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        private async void OnStartCloudPresetClick(object sender, RoutedEventArgs e)
        {
            var provider = ViewModel.SelectedCloudPresetOption;
            if (provider == null)
            {
                await ShowSimpleMessageAsync(I18n.GetString("CloudOnboarding_NoProvider"));
                return;
            }

            if (!await AppDialogService.Default.ConfirmAsync(
                    I18n.GetString("CloudOnboarding_ConfirmTitle"),
                    I18n.Format(
                        "CloudOnboarding_ConfirmContent",
                        provider.DisplayName,
                        provider.Description),
                    I18n.GetString("CloudOnboarding_ConfirmPrimary"),
                    this.XamlRoot))
            {
                return;
            }

            await ViewModel.StartCloudPresetAsync();
            Bindings.Update();
        }

        private Task ShowSimpleMessageAsync(string message) =>
            AppDialogService.Default.ShowMessageAsync(
                I18n.GetString("Common_Tip"),
                message,
                this.XamlRoot,
                I18n.GetString("Common_Close"));
    }
}
