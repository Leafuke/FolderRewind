using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NavigationAnimationPolicyTests
{
    [TestMethod]
    [DataRow(SponsorNavigationAnimationKind.None)]
    [DataRow(SponsorNavigationAnimationKind.Entrance)]
    [DataRow(SponsorNavigationAnimationKind.Slide)]
    [DataRow(SponsorNavigationAnimationKind.DrillIn)]
    public void EntitlementSystemMotionAndInitialPageOverrideTheStoredPreference(SponsorNavigationAnimationKind preference)
    {
        Assert.AreEqual(preference, NavigationAnimationPolicy.Resolve(preference, unlocked: true, animationsEnabled: true));
        Assert.AreEqual(SponsorNavigationAnimationKind.None, NavigationAnimationPolicy.Resolve(preference, unlocked: false, animationsEnabled: true));
        Assert.AreEqual(SponsorNavigationAnimationKind.None, NavigationAnimationPolicy.Resolve(preference, unlocked: true, animationsEnabled: false));
        Assert.AreEqual(SponsorNavigationAnimationKind.None, NavigationAnimationPolicy.Resolve(preference, unlocked: true, animationsEnabled: true, isInitial: true));
        // The original preference becomes effective again as soon as both gates allow it.
        Assert.AreEqual(preference, NavigationAnimationPolicy.Resolve(preference, unlocked: true, animationsEnabled: true));
    }

    [TestMethod]
    public void UnknownPreferenceCannotEnableAnUnrecognizedTransition()
    {
        Assert.AreEqual(SponsorNavigationAnimationKind.None,
            NavigationAnimationPolicy.Resolve((SponsorNavigationAnimationKind)int.MaxValue, unlocked: true, animationsEnabled: true));
        Assert.AreEqual(SponsorNavigationAnimationKind.None,
            NavigationAnimationPolicy.Normalize((SponsorNavigationAnimationKind)(-1)));
    }
}
