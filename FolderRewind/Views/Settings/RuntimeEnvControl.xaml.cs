using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using PickerViewMode = Windows.Storage.Pickers.PickerViewMode;

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
            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.RuntimeEnv.SevenZip",
                new[] { ".exe" },
                MainWindowService.SuggestedPickerLocation.ComputerFolder,
                viewMode: PickerViewMode.List);
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                ViewModel.ApplySevenZipPath(filePath);
            }
        }

        private async void OnBrowseRcloneClick(object sender, RoutedEventArgs e)
        {
            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.RuntimeEnv.Rclone",
                new[] { ".exe", ".cmd", ".bat", ".ps1" },
                MainWindowService.SuggestedPickerLocation.ComputerFolder,
                viewMode: PickerViewMode.List);
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                ViewModel.ApplyRclonePath(filePath);
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
            var folderPath = await MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.Settings.RuntimeEnv.DefaultBackupRoot",
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                ViewModel.ApplyDefaultBackupRootPath(folderPath);
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
