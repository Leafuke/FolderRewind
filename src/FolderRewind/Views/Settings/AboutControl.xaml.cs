using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;

namespace FolderRewind.Views.Settings
{
    public sealed partial class AboutControl : UserControl
    {
        private readonly SettingsPresentationLifetime _presentation = new();
        internal DeferredSettingsSection AboutSection { get; } = new();

        private void UpdatePresentationBindings() => _presentation.UpdateBindings(Bindings.Update);

        internal void DeactivatePresentation()
        {
            ProductMessages.Deactivate();
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
            TaskObserver.Observe(initialization ?? System.Threading.Tasks.Task.CompletedTask, nameof(AboutControl));
        }


        private void ApplyDeferredCardStyles()
        {
            SettingsDeferredContent.ApplyCardStyles(AboutBody, AboutExpander);
        }

        private void RefreshCreatedSections()
        {
            if (AboutSection.IsCreated) EnsureSection(AboutSection, SettingsSection.About);
        }

        private void OnAboutExpanded(object? sender, object e) => EnsureSection(AboutSection, SettingsSection.About);

        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public AboutControl()
        {
            this.InitializeComponent();
            Unloaded += (_, _) => DeactivatePresentation();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            _presentation.Activate();
            ProductMessages.Activate();
            DocumentLink.NavigateUri = new Uri(OfficialLinksService.GetOfficialWebsiteUrl());
            UpdatePresentationBindings();
            RefreshCreatedSections();
        }

        internal void ShowProductTarget(FolderRewind.Models.ProductSettingsNavigationParameter target) => ProductMessages.ShowTarget(target);

        private void OnJoinGroupButtonClick(object sender, RoutedEventArgs e)
            => FlyoutBase.ShowAttachedFlyout(sender as FrameworkElement);
    }
}
