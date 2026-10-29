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

        private void OnStartCloudPresetClick(object sender, RoutedEventArgs e) => ViewModel.OpenCloudPreset();

        private Task ShowSimpleMessageAsync(string message) =>
            AppDialogService.Default.ShowMessageAsync(
                I18n.GetString("Common_Tip"),
                message,
                this.XamlRoot,
                I18n.GetString("Common_Close"));
    }
}
