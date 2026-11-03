using FolderRewind.Models;
using System;

namespace FolderRewind.Services;

internal static class NavigationAnimationPolicy
{
    internal static SponsorNavigationAnimationKind Normalize(SponsorNavigationAnimationKind preference)
        => Enum.IsDefined(preference) ? preference : SponsorNavigationAnimationKind.None;

    internal static SponsorNavigationAnimationKind Resolve(
        SponsorNavigationAnimationKind preference, bool unlocked, bool animationsEnabled, bool isInitial = false)
        => unlocked && animationsEnabled && !isInitial ? Normalize(preference) : SponsorNavigationAnimationKind.None;
}
