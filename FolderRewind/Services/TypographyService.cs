using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;

namespace FolderRewind.Services
{
    public static class TypographyService
    {
        public static void ApplyTypography(GlobalSettings settings)
        {
            if (settings == null) return;

            var familyName = string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI Variable" : settings.FontFamily;
            var baseSize = settings.BaseFontSize;
            if (double.IsNaN(baseSize) || baseSize <= 0) baseSize = 14;
            baseSize = Math.Clamp(baseSize, 12, 20);

            try
            {
                var fontFamily = new FontFamily(familyName);
                Application.Current.Resources["ControlContentThemeFontFamily"] = fontFamily;
                Application.Current.Resources["ContentControlThemeFontFamily"] = fontFamily;
                Application.Current.Resources["TextControlThemeFontFamily"] = fontFamily;
                Application.Current.Resources["DefaultTextBlockFontFamily"] = fontFamily;
            }
            catch
            {
                // ignore invalid font names
            }

            Application.Current.Resources["ControlContentThemeFontSize"] = baseSize;
            Application.Current.Resources["TextControlThemeFontSize"] = baseSize;
        }
    }
}
