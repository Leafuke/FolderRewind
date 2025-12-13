using Microsoft.UI.Xaml;

namespace FolderRewind.Services
{
    public static class ThemeService
    {
        public static event System.Action<ElementTheme>? ThemeChanged;

        public static ElementTheme GetCurrentTheme()
        {
            var idx = ConfigService.CurrentConfig?.GlobalSettings?.ThemeIndex ?? 0;
            return idx == 0 ? ElementTheme.Dark : ElementTheme.Light;
        }

        public static void ApplyThemeToWindow(Window? window)
        {
            if (window?.Content is FrameworkElement root)
            {
                root.RequestedTheme = GetCurrentTheme();
            }
        }

        public static void NotifyThemeChanged()
        {
            ThemeChanged?.Invoke(GetCurrentTheme());
        }
    }
}
