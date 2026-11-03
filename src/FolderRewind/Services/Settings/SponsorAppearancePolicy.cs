using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal enum SponsorAppearanceError { Locked, NameRequired, DuplicateName, MissingScheme, UnknownTheme, Busy }

internal sealed class SponsorAppearanceException(SponsorAppearanceError error) : InvalidOperationException(error.ToString())
{
    public SponsorAppearanceError Error { get; } = error;
}

internal static class SponsorAppearancePolicy
{
    internal static IReadOnlyList<SponsorThemeDefinition> Themes { get; } = Array.AsReadOnly(new[]
    {
        new SponsorThemeDefinition("paper", 5, 0),
        new SponsorThemeDefinition("forest", 3, 0),
        new SponsorThemeDefinition("night", 6, 1)
    });

    internal static SponsorAppearanceSnapshot Normalize(SponsorAppearanceSnapshot value)
    {
        var themeId = Themes.Any(theme => theme.Id == value.ThemeId) ? value.ThemeId : string.Empty;
        var kind = Enum.IsDefined(value.BackgroundKind) ? value.BackgroundKind : SponsorBackgroundKind.Image;
        var invalidGradient = kind == SponsorBackgroundKind.ThemeGradient && themeId.Length == 0;
        return value with
        {
            ThemeId = themeId,
            AccentColorIndex = Math.Clamp(value.AccentColorIndex, 0, 6),
            BackdropIndex = Math.Clamp(value.BackdropIndex, 0, 1),
            TitleText = value.TitleText?.Trim() ?? string.Empty,
            TitleIconGlyph = string.IsNullOrWhiteSpace(value.TitleIconGlyph) ? "\uE8B7" : value.TitleIconGlyph,
            BackgroundKind = invalidGradient ? SponsorBackgroundKind.Image : kind,
            BackgroundEnabled = value.BackgroundEnabled && !invalidGradient,
            BackgroundImagePath = value.BackgroundImagePath?.Trim() ?? string.Empty,
            BackgroundStretchIndex = Math.Clamp(value.BackgroundStretchIndex, 0, 2),
            BackgroundOpacity = Unit(value.BackgroundOpacity, 0.28),
            BackgroundOverlayOpacity = Unit(value.BackgroundOverlayOpacity, 0.62),
            NavigationAnimation = NavigationAnimationPolicy.Normalize(value.NavigationAnimation)
        };
    }

    internal static SponsorAppearanceState Normalize(SponsorAppearanceState state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemes = new List<SponsorAppearanceScheme>();
        foreach (var scheme in state.Schemes ?? Array.Empty<SponsorAppearanceScheme>())
        {
            if (scheme is null || string.IsNullOrWhiteSpace(scheme.Id) || scheme.Id.StartsWith("theme:", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(scheme.Name) || scheme.Appearance is null
                || ids.Contains(scheme.Id) || names.Contains(scheme.Name.Trim())) continue;
            ids.Add(scheme.Id);
            names.Add(scheme.Name.Trim());
            schemes.Add(scheme with { Name = scheme.Name.Trim(), Appearance = Normalize(scheme.Appearance) });
        }
        var activeId = state.ActiveSchemeId ?? string.Empty;
        if (!ids.Contains(activeId) && !Themes.Any(theme => activeId == ThemeSchemeId(theme.Id))) activeId = string.Empty;
        return new(Normalize(state.Current), activeId, schemes);
    }

    internal static string ThemeSchemeId(string themeId) => "theme:" + themeId;

    internal static SponsorAppearanceSnapshot ApplyTheme(SponsorAppearanceSnapshot current, string themeId)
    {
        var theme = Themes.FirstOrDefault(theme => theme.Id == themeId)
            ?? throw new SponsorAppearanceException(SponsorAppearanceError.UnknownTheme);
        return current with
        {
            ThemeId = theme.Id,
            AccentColorIndex = theme.AccentColorIndex,
            BackdropIndex = theme.BackdropIndex,
            BackgroundKind = SponsorBackgroundKind.ThemeGradient,
            BackgroundEnabled = true,
            BackgroundOpacity = 0.82,
            BackgroundOverlayOpacity = 0.18
        };
    }

    internal static string ValidateName(string? name, IReadOnlyList<SponsorAppearanceScheme> schemes, string? exceptId = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new SponsorAppearanceException(SponsorAppearanceError.NameRequired);
        if (schemes.Any(scheme => scheme.Id != exceptId && string.Equals(scheme.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new SponsorAppearanceException(SponsorAppearanceError.DuplicateName);
        return trimmed;
    }

    internal static bool IsModified(SponsorAppearanceState state)
    {
        var scheme = state.Schemes.FirstOrDefault(scheme => scheme.Id == state.ActiveSchemeId);
        if (scheme is not null) return state.Current != scheme.Appearance;
        var theme = Themes.FirstOrDefault(theme => ThemeSchemeId(theme.Id) == state.ActiveSchemeId);
        return theme is not null && ApplyTheme(state.Current, theme.Id) != state.Current;
    }

    internal static bool ShowGradient(string themeId, SponsorBackgroundKind kind, bool enabled, bool unlocked, bool highContrast)
        => enabled && unlocked && !highContrast && kind == SponsorBackgroundKind.ThemeGradient && Themes.Any(theme => theme.Id == themeId);

    private static double Unit(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}
