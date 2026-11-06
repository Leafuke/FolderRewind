using System;
using System.Collections.Generic;

namespace FolderRewind.Models;

public enum SponsorBackgroundKind
{
    Image = 0,
    ThemeGradient = 1
}

public enum SponsorNavigationAnimationKind
{
    None = 0,
    Entrance = 1,
    Slide = 2,
    DrillIn = 3
}

/// <summary>A detached appearance value; deliberately excludes general settings and entitlement.</summary>
public sealed record SponsorAppearanceSnapshot(
    string ThemeId = "",
    int AccentColorIndex = 0,
    int BackdropIndex = 0,
    string TitleText = "",
    string TitleIconGlyph = "\uE8B7",
    SponsorBackgroundKind BackgroundKind = SponsorBackgroundKind.Image,
    bool BackgroundEnabled = false,
    string BackgroundImagePath = "",
    int BackgroundStretchIndex = 0,
    double BackgroundOpacity = 0.28,
    double BackgroundOverlayOpacity = 0.62,
    SponsorNavigationAnimationKind NavigationAnimation = SponsorNavigationAnimationKind.None);

public sealed record SponsorAppearanceScheme
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public SponsorAppearanceSnapshot Appearance { get; init; } = new();
}

internal sealed record SponsorThemeDefinition(string Id, int AccentColorIndex, int BackdropIndex);

internal sealed record SponsorAppearanceState(
    SponsorAppearanceSnapshot Current,
    string ActiveSchemeId,
    IReadOnlyList<SponsorAppearanceScheme> Schemes);
