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
        private readonly SettingsPresentationLifetime _presentation = new();
        internal DeferredSettingsSection PresetsSection { get; } = new();

        private void UpdatePresentationBindings() => _presentation.UpdateBindings(Bindings.Update);

        internal void DeactivatePresentation()
        {
            _presentation.Deactivate();
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
            TaskObserver.Observe(initialization ?? System.Threading.Tasks.Task.CompletedTask, nameof(PresetSettingsControl));
        }


        private void ApplyDeferredCardStyles()
        {
            SettingsDeferredContent.ApplyCardStyles(PresetsBody, PresetsExpander);
        }

        private void RefreshCreatedSections()
        {
            if (PresetsSection.IsCreated) EnsureSection(PresetsSection, SettingsSection.Presets);
        }

        private void OnPresetsExpanded(object? sender, object e) => EnsureSection(PresetsSection, SettingsSection.Presets);

        internal void ShowPresets()
        {
            EnsureSection(PresetsSection, SettingsSection.Presets);
            PresetsExpander.IsExpanded = true;
            PresetsExpander.StartBringIntoView();
        }

        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public PresetSettingsControl()
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

        private void OnStartCloudPresetClick(object sender, RoutedEventArgs e) => ViewModel.OpenCloudPreset();

        private Task ShowSimpleMessageAsync(string message) =>
            AppDialogService.Default.ShowMessageAsync(
                I18n.GetString("Common_Tip"),
                message,
                this.XamlRoot,
                I18n.GetString("Common_Close"));
    }
}
