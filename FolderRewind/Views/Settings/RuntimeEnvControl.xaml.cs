using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage.Pickers;

namespace FolderRewind.Views.Settings
{
    public sealed partial class RuntimeEnvControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public RuntimeEnvControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        private async void OnBrowse7zClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ViewModel.ApplySevenZipPath(file.Path);
            }
        }

        private async void OnBrowseRcloneClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".ps1");
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ViewModel.ApplyRclonePath(file.Path);
            }
        }

        private void OnDefaultCloudRemoteBasePathChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                ViewModel.ApplyDefaultCloudRemoteBasePath(tb.Text);
            }
        }

        private async void OnBrowseDefaultBackupRootClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            MainWindowService.InitializePicker(picker);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.ApplyDefaultBackupRootPath(folder.Path);
            }
        }

        private void OnAutoDownloadMissingCloudBackupsBeforeRestoreToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleAutoDownloadMissingCloudBackupsBeforeRestoreToggled(ts.IsOn);
            }
        }

        private void OnAppUpdateSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleAppUpdateSourceChanged(cb.SelectedIndex);
            }

            Bindings.Update();
        }

        private void OnAppUpdateAutoFallbackToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleAppUpdateAutoFallbackToggled(ts.IsOn);
            }
        }

        private void OnAppUpdateCustomMirrorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                ViewModel.HandleAppUpdateCustomMirrorChanged(tb.Text);
            }
        }
    }
}
