using FolderRewind.Models;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.Services;

internal static class SponsorAppearanceService
{
    internal static SponsorAppearanceController Controller { get; } = new(
        () => Capture(ConfigService.CurrentConfig.GlobalSettings),
        state => Apply(ConfigService.CurrentConfig.GlobalSettings, state),
        () => ConfigService.SaveAsync(),
        () => SponsorService.IsUnlocked,
        () => I18n.GetString("Appearance_SaveFailed"));

    internal static SponsorAppearanceState Capture(GlobalSettings settings) => new(
        new SponsorAppearanceSnapshot
        {
            ThemeId = settings.SponsorThemeId,
            AccentColorIndex = settings.SponsorAccentColorIndex,
            BackdropIndex = settings.SponsorBackdropIndex,
            TitleText = settings.SponsorTitleText,
            TitleIconGlyph = settings.SponsorTitleIconGlyph,
            BackgroundKind = settings.SponsorBackgroundKind,
            BackgroundEnabled = settings.SponsorBackgroundEnabled,
            BackgroundImagePath = settings.SponsorBackgroundImagePath,
            BackgroundStretchIndex = settings.SponsorBackgroundStretchIndex,
            BackgroundOpacity = settings.SponsorBackgroundImageOpacity,
            BackgroundOverlayOpacity = settings.SponsorBackgroundOverlayOpacity,
            NavigationAnimation = settings.SponsorNavigationAnimation
        }, settings.SponsorActiveSchemeId, settings.SponsorAppearanceSchemes.ToArray());

    internal static void Apply(GlobalSettings settings, SponsorAppearanceState state)
    {
        var value = state.Current;
        settings.SponsorThemeId = value.ThemeId;
        settings.SponsorAccentColorIndex = value.AccentColorIndex;
        settings.SponsorBackdropIndex = value.BackdropIndex;
        settings.SponsorTitleText = value.TitleText;
        settings.SponsorTitleIconGlyph = value.TitleIconGlyph;
        settings.SponsorBackgroundKind = value.BackgroundKind;
        settings.SponsorBackgroundEnabled = value.BackgroundEnabled;
        settings.SponsorBackgroundImagePath = value.BackgroundImagePath;
        settings.SponsorBackgroundStretchIndex = value.BackgroundStretchIndex;
        settings.SponsorBackgroundImageOpacity = value.BackgroundOpacity;
        settings.SponsorBackgroundOverlayOpacity = value.BackgroundOverlayOpacity;
        settings.SponsorNavigationAnimation = value.NavigationAnimation;
        settings.SponsorActiveSchemeId = state.ActiveSchemeId;
        if (!settings.SponsorAppearanceSchemes.SequenceEqual(state.Schemes))
            settings.SponsorAppearanceSchemes = new ObservableCollection<SponsorAppearanceScheme>(state.Schemes);
    }

    internal static void Normalize(GlobalSettings settings)
        => Apply(settings, SponsorAppearancePolicy.Normalize(Capture(settings)));
}
