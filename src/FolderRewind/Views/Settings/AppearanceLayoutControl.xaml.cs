using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Views.Settings
{
    public sealed partial class AppearanceLayoutControl : UserControl
    {
        private readonly SettingsPresentationLifetime _presentation = new();
        internal DeferredSettingsSection AppearanceSection { get; } = new();
        internal DeferredSettingsSection SponsorSection { get; } = new();

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
                if (LanguageCombo is not null) LanguageCombo.SelectedIndex = ViewModel.GetLanguageSelectedIndex();
            });
            TaskObserver.Observe(initialization ?? System.Threading.Tasks.Task.CompletedTask, nameof(AppearanceLayoutControl));
        }


        private void ApplyDeferredCardStyles()
        {
            SettingsDeferredContent.ApplyCardStyles(SponsorBody, SponsorExpander);
            SettingsDeferredContent.ApplyCardStyles(AppearanceBody, AppearanceExpander);
        }

        private void RefreshCreatedSections()
        {
            if (AppearanceSection.IsCreated) EnsureSection(AppearanceSection, SettingsSection.Appearance);
            if (SponsorSection.IsCreated) EnsureSection(SponsorSection, SettingsSection.Sponsor);
        }

        private void OnAppearanceExpanded(object? sender, object e) => EnsureSection(AppearanceSection, SettingsSection.Appearance);

        private void OnSponsorExpanded(object? sender, object e) => EnsureSection(SponsorSection, SettingsSection.Sponsor);

        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        private bool _isInitializingLanguage;

        public AppearanceLayoutControl()
        {
            this.InitializeComponent();
            Unloaded += (_, _) => DeactivatePresentation();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            _presentation.Activate();

            // Initialize language combo
            _isInitializingLanguage = true;
            try
            {
                if (LanguageCombo != null)
                {
                    LanguageCombo.SelectedIndex = ViewModel.GetLanguageSelectedIndex();
                }
            }
            finally
            {
                _isInitializingLanguage = false;
            }

            UpdatePresentationBindings();
            RefreshCreatedSections();
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (_isInitializingLanguage) return;

            if (sender is ComboBox cb)
            {
                ViewModel.HandleLanguageChanged(cb.SelectedIndex);
            }
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleThemeChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorAccentChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleSponsorAccentChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorBackdropChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleSponsorBackdropChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorTitleTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is TextBox tb)
            {
                ViewModel.HandleSponsorTitleTextChanged(tb.Text);
            }
        }

        private void OnSponsorTitleIconChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb && cb.SelectedItem is string glyph)
            {
                ViewModel.HandleSponsorTitleIconChanged(glyph);
            }
        }

        private async void OnChooseSponsorBackgroundClick(object sender, RoutedEventArgs e)
        {
            var generation = _presentation.Generation;
            try
            {
                var filePath = await MainWindowService.PickFilePathAsync(
                    string.Empty,
                    "FolderRewind.Settings.Appearance.SponsorBackground",
                    new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" },
                    MainWindowService.SuggestedPickerLocation.PicturesLibrary);
                if (!_presentation.IsCurrent(generation)) return;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                await ViewModel.ApplySponsorBackgroundImageAsync(filePath);
                if (_presentation.IsCurrent(generation)) UpdatePresentationBindings();
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Sponsor_Log_BackgroundPickerFailed", ex.Message), nameof(AppearanceLayoutControl), ex);
                NotificationService.ShowError(I18n.Format("Sponsor_BackgroundPickerFailed", ex.Message), I18n.GetString("Sponsor_Title"));
            }
        }

        private void OnSponsorBackgroundEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleSponsorBackgroundEnabledToggled(ts.IsOn);
            }
        }

        private void OnSponsorBackgroundStretchChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleSponsorBackgroundStretchChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorBackgroundImageOpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            ViewModel.HandleSponsorBackgroundImageOpacityChanged(e.NewValue);
        }

        private void OnSponsorBackgroundOverlayOpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            ViewModel.HandleSponsorBackgroundOverlayOpacityChanged(e.NewValue);
        }

        private void OnCompletionSoundChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleCompletionSoundChanged(cb.SelectedIndex);
            }
        }

        private async void OnChooseCustomCompletionSoundClick(object sender, RoutedEventArgs e)
        {
            var generation = _presentation.Generation;
            try
            {
                var filePath = await MainWindowService.PickFilePathAsync(
                    string.Empty,
                    "FolderRewind.Settings.Appearance.CompletionSound",
                    new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" },
                    MainWindowService.SuggestedPickerLocation.MusicLibrary);
                if (!_presentation.IsCurrent(generation)) return;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                await ViewModel.ApplyCustomCompletionSoundAsync(filePath);
                if (_presentation.IsCurrent(generation)) UpdatePresentationBindings();
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("CompletionSound_Log_CustomPickerFailed", ex.Message), nameof(AppearanceLayoutControl), ex);
                NotificationService.ShowError(I18n.Format("CompletionSound_CustomPickerFailed", ex.Message), I18n.GetString("Sponsor_Title"));
            }
        }

        private void OnFontSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;

            ViewModel.HandleFontSizeChanged(e.NewValue);
        }

        private void OnHistoryColorsToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleHistoryColorsToggled(ts.IsOn);
            }
        }

        private void OnStartupSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (ReferenceEquals(sender, StartupWidthBox))
            {
                ViewModel.HandleStartupSizeChanged(isWidth: true, newValue: e.NewValue);
            }
            else if (ReferenceEquals(sender, StartupHeightBox))
            {
                ViewModel.HandleStartupSizeChanged(isWidth: false, newValue: e.NewValue);
            }
        }

        private void OnApplyStartupSizeClick(object sender, RoutedEventArgs e)
        {
            ViewModel.HandleApplyStartupSize();
        }
    }
}
