using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FolderRewind.ViewModels
{
    public sealed partial class SettingsPageViewModel : ViewModelBase, IDisposable
    {
        public void HandleThemeChanged(int selectedIndex)
        {
            Settings.ThemeIndex = Math.Clamp(selectedIndex, 0, 2);
            MainWindowService.ApplyCurrentTheme();
            ThemeService.NotifyThemeChanged();
            _isDirty = true;
        }

        public void HandleSponsorAccentChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            Settings.SponsorAccentColorIndex = Math.Clamp(selectedIndex, 0, ThemeService.SponsorAccentPresetCount - 1);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackdropChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            Settings.SponsorBackdropIndex = Math.Clamp(selectedIndex, 0, 1);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public int SponsorNavigationAnimationIndex => (int)NavigationAnimationPolicy.Normalize(Settings.SponsorNavigationAnimation);

        public void HandleSponsorNavigationAnimationChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning || selectedIndex < 0
                || !Enum.IsDefined((SponsorNavigationAnimationKind)selectedIndex)) return;
            var value = (SponsorNavigationAnimationKind)selectedIndex;
            if (Settings.SponsorNavigationAnimation == value) return;
            Settings.SponsorNavigationAnimation = value;
            _isDirty = true;
        }

        public void HandleSponsorTitleTextChanged(string? titleText)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            Settings.SponsorTitleText = titleText?.Trim() ?? string.Empty;
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorTitleIconChanged(string? glyph)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning || string.IsNullOrWhiteSpace(glyph))
            {
                return;
            }

            Settings.SponsorTitleIconGlyph = glyph;
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public async Task ApplySponsorBackgroundImageAsync(string path)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            if (await SponsorPersonalizationService.ApplyBackgroundImageAsync(path))
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public async Task ClearSponsorBackgroundAsync()
        {
            if (await SponsorPersonalizationService.ClearBackgroundImageAsync())
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void HandleSponsorBackgroundEnabledToggled(bool isOn)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            Settings.SponsorBackgroundEnabled = isOn;
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackgroundStretchChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            Settings.SponsorBackgroundStretchIndex = Math.Clamp(selectedIndex, 0, 2);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackgroundImageOpacityChanged(double newValue)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning || double.IsNaN(newValue))
            {
                return;
            }

            Settings.SponsorBackgroundImageOpacity = Math.Clamp(newValue / 100d, 0, 1);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
            OnPropertyChanged(nameof(SponsorBackgroundImageOpacityPercent));
        }

        public void HandleSponsorBackgroundOverlayOpacityChanged(double newValue)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning || double.IsNaN(newValue))
            {
                return;
            }

            Settings.SponsorBackgroundOverlayOpacity = Math.Clamp(newValue / 100d, 0, 1);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
            OnPropertyChanged(nameof(SponsorBackgroundOverlayOpacityPercent));
        }

        public void HandleCompletionSoundChanged(int selectedIndex)
        {
            Settings.CompletionSoundIndex = Math.Clamp(selectedIndex, 0, CompletionSoundService.PresetCount - 1);
            _isDirty = true;
        }

        public void PreviewCompletionSound()
        {
            CompletionSoundService.PreviewConfiguredSound();
        }

        public async Task ApplyCustomCompletionSoundAsync(string path)
        {
            if (!SponsorService.IsUnlocked || _isAppearanceOperationRunning)
            {
                return;
            }

            if (await CompletionSoundService.ApplyCustomSoundAsync(path))
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public async Task ClearCustomCompletionSoundAsync()
        {
            if (await CompletionSoundService.ClearCustomSoundAsync())
            {
                OnPropertyChanged(nameof(Settings));
            }
        }


        private static string GetAppVersionString()
        {
            var version = AppRuntimeInfo.GetApplicationVersion();
            return version == null
                ? "Version (unknown)"
                : $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }


        private void RefreshSponsorOptionLists()
        {
            BuiltinAppearanceThemeNames.Clear();
            foreach (var theme in SponsorAppearancePolicy.Themes)
                BuiltinAppearanceThemeNames.Add(I18n.GetString("Appearance_Theme_" + theme.Id));
            SponsorAccentPresets.Clear();
            for (var i = 0; i < ThemeService.SponsorAccentPresetCount; i++)
            {
                SponsorAccentPresets.Add(ThemeService.GetAccentPresetName(i));
            }

            SponsorBackdropPresets.Clear();
            SponsorBackdropPresets.Add(ThemeService.GetBackdropName(0));
            SponsorBackdropPresets.Add(ThemeService.GetBackdropName(1));

            SponsorNavigationAnimationPresets.Clear();
            foreach (var animation in Enum.GetValues<SponsorNavigationAnimationKind>())
                SponsorNavigationAnimationPresets.Add(I18n.GetString("Sponsor_NavigationAnimation_" + animation));

            SponsorTitleIconGlyphs.Clear();
            foreach (var glyph in IconCatalog.TitleIconGlyphs)
            {
                SponsorTitleIconGlyphs.Add(glyph);
            }

            SponsorBackgroundStretchModes.Clear();
            SponsorBackgroundStretchModes.Add(I18n.GetString("Sponsor_BackgroundStretch_UniformToFill"));
            SponsorBackgroundStretchModes.Add(I18n.GetString("Sponsor_BackgroundStretch_Uniform"));
            SponsorBackgroundStretchModes.Add(I18n.GetString("Sponsor_BackgroundStretch_Fill"));

            CompletionSoundPresets.Clear();
            for (var i = 0; i < CompletionSoundService.PresetCount; i++)
            {
                CompletionSoundPresets.Add(CompletionSoundService.GetPresetName(i));
            }
            RefreshAppearanceSchemeState();
        }


        private async Task RunSponsorOperationAsync(Func<Task<SponsorOperationResult>> operation)
        {
            if (IsSponsorOperationRunning)
            {
                return;
            }

            IsSponsorOperationRunning = true;
            try
            {
                var result = await operation();
                if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
                {
                    LogService.LogWarning(result.Message, nameof(SettingsPageViewModel));
                }

                RefreshSponsorState();
            }
            finally
            {
                IsSponsorOperationRunning = false;
            }
        }
    }
}
