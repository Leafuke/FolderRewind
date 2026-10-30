namespace FolderRewind.Services;

internal readonly record struct PersonalizationThemeState(int AccentIndex, bool BackgroundEnabled);

/// <summary>Derives effective appearance without changing the saved sponsor preferences.</summary>
internal static class PersonalizationThemePolicy
{
    internal static PersonalizationThemeState Resolve(int accentIndex, bool backgroundEnabled, bool unlocked, bool highContrast)
        => unlocked && !highContrast
            ? new(accentIndex, backgroundEnabled)
            : new(0, false);
}
