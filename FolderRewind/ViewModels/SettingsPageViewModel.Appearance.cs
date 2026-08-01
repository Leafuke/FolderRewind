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
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorAccentColorIndex = Math.Clamp(selectedIndex, 0, ThemeService.SponsorAccentPresetCount - 1);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackdropChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorBackdropIndex = Math.Clamp(selectedIndex, 0, 1);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorTitleTextChanged(string? titleText)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorTitleText = titleText?.Trim() ?? string.Empty;
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorTitleIconChanged(string? glyph)
        {
            if (!SponsorService.IsUnlocked || string.IsNullOrWhiteSpace(glyph))
            {
                return;
            }

            Settings.SponsorTitleIconGlyph = glyph;
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public async Task ApplySponsorBackgroundImageAsync(string path)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            if (await SponsorPersonalizationService.ApplyBackgroundImageAsync(path))
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void ClearSponsorBackground()
        {
            if (SponsorPersonalizationService.ClearBackgroundImage())
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void HandleSponsorBackgroundEnabledToggled(bool isOn)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorBackgroundEnabled = isOn;
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackgroundStretchChanged(int selectedIndex)
        {
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            Settings.SponsorBackgroundStretchIndex = Math.Clamp(selectedIndex, 0, 2);
            _isDirty = true;
            MainWindowService.ApplySponsorVisuals();
        }

        public void HandleSponsorBackgroundImageOpacityChanged(double newValue)
        {
            if (!SponsorService.IsUnlocked || double.IsNaN(newValue))
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
            if (!SponsorService.IsUnlocked || double.IsNaN(newValue))
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
            if (!SponsorService.IsUnlocked)
            {
                return;
            }

            if (await CompletionSoundService.ApplyCustomSoundAsync(path))
            {
                OnPropertyChanged(nameof(Settings));
            }
        }

        public void ClearCustomCompletionSound()
        {
            if (CompletionSoundService.ClearCustomSound())
            {
                OnPropertyChanged(nameof(Settings));
            }
        }


        private void EnsureFontFamiliesLoaded()
        {
            // 先同步放入基础字体，保证设置页首帧可交互。
            ApplyFontFamilies(GetFallbackFontFamilies(), persistWhenEmpty: false);

            if (_fontFamiliesLoading)
            {
                return;
            }

            _fontFamiliesLoading = true;
            _ = LoadFontFamiliesAsync();
        }

        private async Task LoadFontFamiliesAsync()
        {
            try
            {
                IReadOnlyList<string>? cached;
                lock (FontCacheLock)
                {
                    cached = _cachedInstalledFontFamilies;
                }

                if (cached == null)
                {
                    cached = await Task.Run(() =>
                    {
                        try
                        {
                            return FontService.GetInstalledFontFamilies();
                        }
                        catch
                        {
                            return GetFallbackFontFamilies();
                        }
                    }).ConfigureAwait(false);

                    lock (FontCacheLock)
                    {
                        _cachedInstalledFontFamilies ??= cached;
                        cached = _cachedInstalledFontFamilies;
                    }
                }

                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    ApplyFontFamilies(cached ?? GetFallbackFontFamilies(), persistWhenEmpty: true);
                });
            }
            finally
            {
                _fontFamiliesLoading = false;
            }
        }

        private IReadOnlyList<string> GetFallbackFontFamilies()
        {
            return new[] { "Segoe UI Variable", "Segoe UI", "Microsoft YaHei", "Microsoft YaHei UI" };
        }

        private void ApplyFontFamilies(IReadOnlyList<string> fonts, bool persistWhenEmpty)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in fonts)
            {
                if (!string.IsNullOrWhiteSpace(f))
                {
                    set.Add(f);
                }
            }

            foreach (var fallback in GetFallbackFontFamilies())
            {
                set.Add(fallback);
            }

            if (!string.IsNullOrWhiteSpace(Settings.FontFamily))
            {
                set.Add(Settings.FontFamily);
            }

            FontFamilies.Clear();
            foreach (var family in set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                FontFamilies.Add(family);
            }

            if (!persistWhenEmpty || !string.IsNullOrWhiteSpace(Settings.FontFamily))
            {
                return;
            }

            var preferred = PickPreferredFont(set);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                Settings.FontFamily = preferred;
                ConfigService.Save();
            }
        }

        private string PickPreferredFont(HashSet<string> availableFonts)
        {
            var preferChinese = LanguageSettingPolicy.IsChinese(Settings.Language);

            if (preferChinese)
            {
                if (availableFonts.Contains("Microsoft YaHei UI"))
                {
                    return "Microsoft YaHei UI";
                }

                if (availableFonts.Contains("Microsoft YaHei"))
                {
                    return "Microsoft YaHei";
                }
            }

            if (availableFonts.Contains("Segoe UI Variable"))
            {
                return "Segoe UI Variable";
            }

            if (availableFonts.Contains("Segoe UI"))
            {
                return "Segoe UI";
            }

            return availableFonts.FirstOrDefault() ?? "Segoe UI";
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
            SponsorAccentPresets.Clear();
            for (var i = 0; i < ThemeService.SponsorAccentPresetCount; i++)
            {
                SponsorAccentPresets.Add(ThemeService.GetAccentPresetName(i));
            }

            SponsorBackdropPresets.Clear();
            SponsorBackdropPresets.Add(ThemeService.GetBackdropName(0));
            SponsorBackdropPresets.Add(ThemeService.GetBackdropName(1));

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
