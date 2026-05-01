using FolderRewind.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace FolderRewind.Services
{
    public static class ThemeService
    {
        public static event System.Action<ElementTheme>? ThemeChanged;

        public const int SponsorAccentPresetCount = 7;

        public static ElementTheme GetCurrentTheme()
        {
            var idx = ConfigService.CurrentConfig?.GlobalSettings?.ThemeIndex ?? 0;
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
            if (window?.Content is FrameworkElement root)
            {
                root.RequestedTheme = GetCurrentTheme();
            }
        }

        public static void ApplyPersonalizationToWindow(Window? window)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var unlocked = SponsorService.IsUnlocked;

            ApplyAccentPreset(unlocked ? settings?.SponsorAccentColorIndex ?? 0 : 0);
            ApplyBackdrop(window, unlocked ? settings?.SponsorBackdropIndex ?? 0 : 0);
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
            if (dialog == null) return;
            dialog.RequestedTheme = GetCurrentTheme();
        }

        /// <summary>
        /// 为 FrameworkElement 应用当前主题
        /// </summary>
        public static void ApplyThemeToElement(FrameworkElement? element)
        {
            if (element == null) return;
            element.RequestedTheme = GetCurrentTheme();
        }

        public static void NotifyThemeChanged()
        {
            ThemeChanged?.Invoke(GetCurrentTheme());
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

        private static void ApplyAccentPreset(int accentIndex)
        {
            var index = Math.Clamp(accentIndex, 0, SponsorAccentPresetCount - 1);
            if (index == 0)
            {
                ClearAccentOverride();
                return;
            }

            var color = GetAccentColor(index);
            var light1 = Blend(color, Colors.White, 0.18);
            var light2 = Blend(color, Colors.White, 0.36);
            var light3 = Blend(color, Colors.White, 0.54);
            var dark1 = Blend(color, Colors.Black, 0.14);
            var dark2 = Blend(color, Colors.Black, 0.28);
            var dark3 = Blend(color, Colors.Black, 0.42);

            SetResource("SystemAccentColor", color);
            SetResource("SystemAccentColorLight1", light1);
            SetResource("SystemAccentColorLight2", light2);
            SetResource("SystemAccentColorLight3", light3);
            SetResource("SystemAccentColorDark1", dark1);
            SetResource("SystemAccentColorDark2", dark2);
            SetResource("SystemAccentColorDark3", dark3);

            SetResource("AccentFillColorDefaultBrush", new SolidColorBrush(color));
            SetResource("AccentFillColorSecondaryBrush", new SolidColorBrush(WithAlpha(color, 0xE6)));
            SetResource("AccentFillColorTertiaryBrush", new SolidColorBrush(WithAlpha(color, 0xCC)));
            SetResource("SystemControlForegroundAccentBrush", new SolidColorBrush(color));
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

        private static void ClearAccentOverride()
        {
            RemoveResource("SystemAccentColor");
            RemoveResource("SystemAccentColorLight1");
            RemoveResource("SystemAccentColorLight2");
            RemoveResource("SystemAccentColorLight3");
            RemoveResource("SystemAccentColorDark1");
            RemoveResource("SystemAccentColorDark2");
            RemoveResource("SystemAccentColorDark3");
            RemoveResource("AccentFillColorDefaultBrush");
            RemoveResource("AccentFillColorSecondaryBrush");
            RemoveResource("AccentFillColorTertiaryBrush");
            RemoveResource("SystemControlForegroundAccentBrush");
        }

        private static Color Blend(Color color, Color target, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(
                color.A,
                (byte)Math.Round(color.R + (target.R - color.R) * amount),
                (byte)Math.Round(color.G + (target.G - color.G) * amount),
                (byte)Math.Round(color.B + (target.B - color.B) * amount));
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static void SetResource(string key, object value)
        {
            Application.Current.Resources[key] = value;
        }

        private static void RemoveResource(string key)
        {
            var resources = Application.Current.Resources;
            if (resources.ContainsKey(key))
            {
                resources.Remove(key);
            }
        }
    }
}
