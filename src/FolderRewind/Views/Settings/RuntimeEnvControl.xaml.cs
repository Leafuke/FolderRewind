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
        private readonly SettingsPresentationLifetime _presentation = new();
        internal DeferredSettingsSection EnvironmentSection { get; } = new();

        private void UpdatePresentationBindings() => _presentation.UpdateBindings(Bindings.Update);

        internal void DeactivatePresentation()
        {
            _presentation.Deactivate();
            OpenListEnvironment?.DeactivatePresentation();
            Bindings.StopTracking();
        }

        private void EnsureSection(DeferredSettingsSection section, SettingsSection kind)
        {
            if (!_presentation.IsActive) return;
            System.Threading.Tasks.Task? initialization = null;
            _presentation.UpdateBindings(() =>
            {
                initialization = ViewModel.InitializeSectionAsync(kind);
                section.Create();
                Bindings.Update();
                ApplyDeferredCardStyles();
            });
            OpenListEnvironment?.ActivatePresentation();
            TaskObserver.Observe(initialization ?? System.Threading.Tasks.Task.CompletedTask, nameof(RuntimeEnvControl));
        }


        private void ApplyDeferredCardStyles()
        {
            SettingsDeferredContent.ApplyCardStyles(EnvironmentBody, RuntimeEnvironmentExpander);
        }

        private void RefreshCreatedSections()
        {
            if (EnvironmentSection.IsCreated) EnsureSection(EnvironmentSection, SettingsSection.Runtime);
        }

        private void OnEnvironmentExpanded(object? sender, object e) => EnsureSection(EnvironmentSection, SettingsSection.Runtime);

        public SettingsPageViewModel ViewModel { get; private set; } = null!;
        private void OnOpenCloudConnections(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("CloudConnections");
        public void ShowOpenList()
        {
            EnsureSection(EnvironmentSection, SettingsSection.Runtime);
            RuntimeEnvironmentExpander.IsExpanded = true;
            OpenListEnvironment.ShowRepairTarget();
        }

        public RuntimeEnvControl()
        {
            this.InitializeComponent();
            Unloaded += (_, _) => DeactivatePresentation();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            _presentation.Activate();
            UpdatePresentationBindings();
            RefreshCreatedSections();
        }

        private async void OnBrowse7zClick(object sender, RoutedEventArgs e)
        {
            var generation = _presentation.Generation;
            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.RuntimeEnv.SevenZip",
                new[] { ".exe" },
                MainWindowService.SuggestedPickerLocation.ComputerFolder,
                viewMode: PickerViewMode.List);
            if (!_presentation.IsCurrent(generation)) return;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                ViewModel.ApplySevenZipPath(filePath);
            }
        }

        private async void OnBrowseRcloneClick(object sender, RoutedEventArgs e)
        {
            var generation = _presentation.Generation;
            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.RuntimeEnv.Rclone",
                new[] { ".exe", ".cmd", ".bat", ".ps1" },
                MainWindowService.SuggestedPickerLocation.ComputerFolder,
                viewMode: PickerViewMode.List);
            if (!_presentation.IsCurrent(generation)) return;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                ViewModel.ApplyRclonePath(filePath);
            }
        }

        private void OnDefaultCloudRemoteBasePathChanged(object sender, TextChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is TextBox tb)
            {
                ViewModel.ApplyDefaultCloudRemoteBasePath(tb.Text);
            }
        }

        private async void OnBrowseDefaultBackupRootClick(object sender, RoutedEventArgs e)
        {
            var generation = _presentation.Generation;
            var folderPath = await MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.Settings.RuntimeEnv.DefaultBackupRoot",
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (!_presentation.IsCurrent(generation)) return;
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                ViewModel.ApplyDefaultBackupRootPath(folderPath);
            }
        }

        private void OnAutoDownloadMissingCloudBackupsBeforeRestoreToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleAutoDownloadMissingCloudBackupsBeforeRestoreToggled(ts.IsOn);
            }
        }

        private void OnAppUpdateSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleAppUpdateSourceChanged(cb.SelectedIndex);
            }

            UpdatePresentationBindings();
        }

        private void OnAppUpdateAutoFallbackToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleAppUpdateAutoFallbackToggled(ts.IsOn);
            }
        }

        private void OnAppUpdateCustomMirrorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is TextBox tb)
            {
                ViewModel.HandleAppUpdateCustomMirrorChanged(tb.Text);
            }
        }
    }
}
