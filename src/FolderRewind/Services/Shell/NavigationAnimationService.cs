using FolderRewind.Models;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace FolderRewind.Services;

/// <summary>Creates a fresh native transition for each main-content navigation.</summary>
internal static class NavigationAnimationService
{
    private static readonly UISettings Settings = new();

    internal static NavigationTransitionInfo CreateTransition(bool isInitial = false, bool isBack = false)
    {
        // Read the OS preference at navigation time so toggling reduced motion needs no restart.
        var effective = NavigationAnimationPolicy.Resolve(
            ConfigService.CurrentConfig.GlobalSettings.SponsorNavigationAnimation,
            SponsorService.IsUnlocked, Settings.AnimationsEnabled, isInitial);
        return effective switch
        {
            SponsorNavigationAnimationKind.Entrance => new EntranceNavigationTransitionInfo(),
            SponsorNavigationAnimationKind.Slide => new SlideNavigationTransitionInfo
            {
                Effect = isBack ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight
            },
            SponsorNavigationAnimationKind.DrillIn => new DrillInNavigationTransitionInfo(),
            _ => new SuppressNavigationTransitionInfo()
        };
    }
}
