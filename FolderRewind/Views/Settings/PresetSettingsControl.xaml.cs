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

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("CloudOnboarding_ConfirmTitle"),
                Content = new TextBlock
                {
                    Text = I18n.Format(
                        "CloudOnboarding_ConfirmContent",
                        provider.DisplayName,
                        provider.Description),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = I18n.GetString("CloudOnboarding_ConfirmPrimary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await TemplateDialogCoordinatorService.ShowAsync(dialog, this.XamlRoot);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.StartCloudPresetAsync();
            Bindings.Update();
        }

        private async Task ShowSimpleMessageAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Common_Tip"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = I18n.GetString("Common_Close"),
                XamlRoot = this.XamlRoot,
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }
    }
}
