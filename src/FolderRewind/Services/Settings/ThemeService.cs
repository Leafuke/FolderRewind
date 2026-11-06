using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace FolderRewind.Services
{
    public static class ThemeService
    {
        public static event System.Action<ElementTheme>? ThemeChanged;

        public const int SponsorAccentPresetCount = 7;

        private static readonly List<WeakReference<FrameworkElement>> ThemeRoots = new();
        private static SponsorAccentResources? _accentResources;
        private static int _appliedAccentIndex;

        public static ElementTheme GetCurrentTheme()
        {
            var idx = ThemeSettingPolicy.Normalize(ConfigService.CurrentConfig?.GlobalSettings?.ThemeIndex);
            return idx switch
            {
                0 => ElementTheme.Dark,
                1 => ElementTheme.Light,
                2 => ElementTheme.Default,
                _ => ElementTheme.Default
            };
        }

        public static void ApplyThemeToWindow(Window? window)
        {
            ApplyThemeToElement(window?.Content as FrameworkElement);
        }

        public static void ApplyPersonalizationToWindow(Window? window)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var unlocked = SponsorService.IsUnlocked;

            var accentChanged = ApplyAccentPreferences();
            ApplyBackdrop(window, unlocked ? settings?.SponsorBackdropIndex ?? 0 : 0);
            if (accentChanged) ThemeChanged?.Invoke(GetCurrentTheme());
        }

        internal static bool ApplyAccentPreferences()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var appearance = PersonalizationThemePolicy.Resolve(settings?.SponsorAccentColorIndex ?? 0,
                settings?.SponsorBackgroundEnabled == true, SponsorService.IsUnlocked, AccessibilityThemeService.IsHighContrast);
            return ApplyAccentPreset(appearance.AccentIndex);
        }

        public static string GetAccentPresetName(int index)
        {
            return Math.Clamp(index, 0, SponsorAccentPresetCount - 1) switch
            {
                1 => I18n.GetString("Sponsor_Accent_Blue"),
                2 => I18n.GetString("Sponsor_Accent_Teal"),
                3 => I18n.GetString("Sponsor_Accent_Green"),
                4 => I18n.GetString("Sponsor_Accent_Pink"),
                5 => I18n.GetString("Sponsor_Accent_Orange"),
                6 => I18n.GetString("Sponsor_Accent_Purple"),
                _ => I18n.GetString("Sponsor_Accent_System")
            };
        }

        public static string GetBackdropName(int index)
        {
            return Math.Clamp(index, 0, 1) == 1
                ? I18n.GetString("Sponsor_Backdrop_Acrylic")
                : I18n.GetString("Sponsor_Backdrop_Mica");
        }

        /// <summary>
        /// 为 ContentDialog 应用当前主题
        /// </summary>
        public static void ApplyThemeToDialog(ContentDialog? dialog)
        {
            ApplyThemeToElement(dialog);
        }

        /// <summary>
        /// 为 FrameworkElement 应用当前主题
        /// </summary>
        public static void ApplyThemeToElement(FrameworkElement? element)
        {
            if (element == null) return;
            element.RequestedTheme = GetCurrentTheme();
            ThemeRoots.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (!ThemeRoots.Exists(reference => reference.TryGetTarget(out var root) && ReferenceEquals(root, element)))
                ThemeRoots.Add(new WeakReference<FrameworkElement>(element));
        }

        public static void NotifyThemeChanged()
        {
            ApplyAccentPreferences();
            var theme = GetCurrentTheme();
            foreach (var reference in ThemeRoots.ToArray())
                if (reference.TryGetTarget(out var root)) root.RequestedTheme = theme;
            ThemeChanged?.Invoke(theme);
        }

        private static void ApplyBackdrop(Window? window, int backdropIndex)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                window.SystemBackdrop = Math.Clamp(backdropIndex, 0, 1) == 1
                    ? new DesktopAcrylicBackdrop()
                    : new MicaBackdrop();
            }
            catch (System.Exception ex)
            {
                LogService.LogWarning(I18n.Format("Sponsor_Log_BackdropApplyFailed", ex.Message), nameof(ThemeService));
            }
        }

        private static bool ApplyAccentPreset(int accentIndex)
        {
            var index = Math.Clamp(accentIndex, 0, SponsorAccentPresetCount - 1);
            if (index == _appliedAccentIndex) return false;

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            if (index == 0)
            {
                if (_accentResources is not null) dictionaries.Remove(_accentResources);
            }
            else
            {
                if (_accentResources is null) _accentResources = new SponsorAccentResources(GetAccentColor(index));
                else _accentResources.Update(GetAccentColor(index));
                if (!dictionaries.Contains(_accentResources)) dictionaries.Add(_accentResources);
            }

            _appliedAccentIndex = index;
            RefreshAccentResources();
            return true;
        }

        private static Color GetAccentColor(int index)
        {
            return index switch
            {
                1 => Color.FromArgb(255, 0, 120, 212),
                2 => Color.FromArgb(255, 0, 153, 188),
                3 => Color.FromArgb(255, 16, 124, 16),
                4 => Color.FromArgb(255, 227, 0, 140),
                5 => Color.FromArgb(255, 202, 80, 16),
                6 => Color.FromArgb(255, 116, 77, 169),
                _ => Color.FromArgb(255, 0, 120, 212)
            };
        }

        private static void RefreshAccentResources()
        {
            ThemeRoots.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in ThemeRoots.ToArray())
            {
                if (!reference.TryGetTarget(out var root)) continue;
                // Adding/removing resources alone does not invalidate an existing ThemeResource reference.
                // Refresh synchronously and preserve Default so following the system theme still works.
                var requestedTheme = root.RequestedTheme;
                root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = requestedTheme;
            }
        }
    }
}
