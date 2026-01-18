using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Services
{
    public static class ThemeService
    {
        public static event System.Action<ElementTheme>? ThemeChanged;

        public static ElementTheme GetCurrentTheme()
        {
            var idx = ConfigService.CurrentConfig?.GlobalSettings?.ThemeIndex ?? 0;
            return idx switch
            {
                0 => ElementTheme.Dark,
                1 => ElementTheme.Light,
                _ => ElementTheme.Light
            };
        }

        public static void ApplyThemeToWindow(Window? window)
        {
            if (window?.Content is FrameworkElement root)
            {
                root.RequestedTheme = GetCurrentTheme();
            }
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
    }
}
