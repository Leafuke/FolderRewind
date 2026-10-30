using Windows.UI.ViewManagement;

namespace FolderRewind.Services;

/// <summary>Forwards contrast changes to presentation code on the injected UI dispatcher.</summary>
internal static class AccessibilityThemeService
{
    private static readonly AccessibilitySettings Settings = new();
    private static readonly UISettings Colors = new();
    private static bool _initialized;

    internal static bool IsHighContrast => Settings.HighContrast;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        // AccessibilitySettings.HighContrastChanged is unavailable in some desktop
        // environments. ColorValuesChanged also reports contrast palette changes.
        Colors.ColorValuesChanged += OnColorValuesChanged;
        ThemeService.NotifyThemeChanged();
    }

    internal static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        Colors.ColorValuesChanged -= OnColorValuesChanged;
    }

    private static void OnColorValuesChanged(UISettings sender, object args)
    {
        UiDispatcherService.Enqueue(() =>
        {
            if (_initialized) ThemeService.NotifyThemeChanged();
        });
    }
}
