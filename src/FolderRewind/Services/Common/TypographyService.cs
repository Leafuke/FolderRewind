using FolderRewind.Models;
using Microsoft.UI.Xaml;
using System;

namespace FolderRewind.Services
{
    public static class TypographyService
    {
        public static void ApplyTypography(GlobalSettings settings)
        {
            if (settings == null) return;

            var baseSize = settings.BaseFontSize;
            if (double.IsNaN(baseSize) || baseSize <= 0) baseSize = 14;
            baseSize = Math.Clamp(baseSize, 12, 20);

            Application.Current.Resources["ControlContentThemeFontSize"] = baseSize;
            Application.Current.Resources["TextControlThemeFontSize"] = baseSize;
        }
    }
}
