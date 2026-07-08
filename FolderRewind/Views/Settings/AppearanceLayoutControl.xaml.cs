using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Specialized;
using System.Linq;

namespace FolderRewind.Views.Settings
{
    public sealed partial class AppearanceLayoutControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        private bool _isInitializingLanguage;
        private bool _isInitializingFont;

        public AppearanceLayoutControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;

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

            // Initialize font family and size
            _isInitializingFont = true;
            try
            {
                SyncFontFamilySelection();
                if (FontSizeBox != null)
                {
                    FontSizeBox.Value = ViewModel.Settings.BaseFontSize;
                }
            }
            finally
            {
                _isInitializingFont = false;
            }

            // Subscribe to font families changes
            ViewModel.FontFamilies.CollectionChanged += OnFontFamiliesCollectionChanged;

            Bindings.Update();
        }

        private void SyncFontFamilySelection()
        {
            if (FontFamilyCombo == null)
            {
                return;
            }

            var current = ViewModel.FontFamilies.FirstOrDefault(
                f => string.Equals(f, ViewModel.Settings.FontFamily, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(current))
            {
                FontFamilyCombo.SelectedItem = current;
                return;
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.Settings.FontFamily))
            {
                FontFamilyCombo.SelectedItem = ViewModel.Settings.FontFamily;
                return;
            }

            FontFamilyCombo.SelectedItem = ViewModel.FontFamilies.FirstOrDefault();
        }

        private void OnFontFamiliesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (FontFamilyCombo == null || _isInitializingFont)
            {
                return;
            }

            _isInitializingFont = true;
            try
            {
                SyncFontFamilySelection();
            }
            finally
            {
                _isInitializingFont = false;
            }
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingLanguage) return;

            if (sender is ComboBox cb)
            {
                ViewModel.HandleLanguageChanged(cb.SelectedIndex);
            }
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleThemeChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorAccentChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleSponsorAccentChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorBackdropChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleSponsorBackdropChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorTitleTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                ViewModel.HandleSponsorTitleTextChanged(tb.Text);
            }
        }

        private void OnSponsorTitleIconChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string glyph)
            {
                ViewModel.HandleSponsorTitleIconChanged(glyph);
            }
        }

        private async void OnChooseSponsorBackgroundClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var filePath = await MainWindowService.PickFilePathAsync(
                    string.Empty,
                    "FolderRewind.Settings.Appearance.SponsorBackground",
                    new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" },
                    MainWindowService.SuggestedPickerLocation.PicturesLibrary);
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                await ViewModel.ApplySponsorBackgroundImageAsync(filePath);
                Bindings.Update();
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Sponsor_Log_BackgroundPickerFailed", ex.Message), nameof(AppearanceLayoutControl), ex);
                NotificationService.ShowError(I18n.Format("Sponsor_BackgroundPickerFailed", ex.Message), I18n.GetString("Sponsor_Title"));
            }
        }

        private void OnSponsorBackgroundEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleSponsorBackgroundEnabledToggled(ts.IsOn);
            }
        }

        private void OnSponsorBackgroundStretchChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleSponsorBackgroundStretchChanged(cb.SelectedIndex);
            }
        }

        private void OnSponsorBackgroundImageOpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            ViewModel.HandleSponsorBackgroundImageOpacityChanged(e.NewValue);
        }

        private void OnSponsorBackgroundOverlayOpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            ViewModel.HandleSponsorBackgroundOverlayOpacityChanged(e.NewValue);
        }

        private void OnCompletionSoundChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleCompletionSoundChanged(cb.SelectedIndex);
            }
        }

        private async void OnChooseCustomCompletionSoundClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var filePath = await MainWindowService.PickFilePathAsync(
                    string.Empty,
                    "FolderRewind.Settings.Appearance.CompletionSound",
                    new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" },
                    MainWindowService.SuggestedPickerLocation.MusicLibrary);
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                await ViewModel.ApplyCustomCompletionSoundAsync(filePath);
                Bindings.Update();
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("CompletionSound_Log_CustomPickerFailed", ex.Message), nameof(AppearanceLayoutControl), ex);
                NotificationService.ShowError(I18n.Format("CompletionSound_CustomPickerFailed", ex.Message), I18n.GetString("Sponsor_Title"));
            }
        }

        private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingFont) return;

            if (sender is ComboBox cb && cb.SelectedItem is string selected)
            {
                ViewModel.HandleFontFamilyChanged(selected);
            }
        }

        private void OnFontSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (_isInitializingFont) return;

            ViewModel.HandleFontSizeChanged(e.NewValue);
        }

        private void OnHistoryColorsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleHistoryColorsToggled(ts.IsOn);
            }
        }

        private void OnStartupSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
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
