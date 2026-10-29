using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SponsorBackgroundImageCachePolicyTests
{
    private static SponsorBackgroundImageState DisplayedState(string path = "C:\\background.png") =>
        new(path, IsEnabled: true, IsSponsorUnlocked: true, FileExists: true);

    [TestMethod]
    public void SameDisplayedStateWithCachedImageDoesNotReload()
    {
        var state = DisplayedState();

        Assert.IsFalse(SponsorBackgroundImageCachePolicy.ShouldReload(
            state,
            state,
            hasCachedImage: true,
            forceReload: false));
    }

    [TestMethod]
    public void SamePathCanBeForceReloadedAfterFileReplacement()
    {
        var state = DisplayedState();

        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldReload(
            state,
            state,
            hasCachedImage: true,
            forceReload: true));
    }

    [TestMethod]
    public void DisplayedStateChangesTriggerReload()
    {
        var previous = DisplayedState();

        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldReload(
            previous,
            DisplayedState("C:\\new-background.jpg"),
            hasCachedImage: true,
            forceReload: false));
        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldReload(
            previous,
            previous,
            hasCachedImage: false,
            forceReload: false));
    }

    [TestMethod]
    public void DisabledOrMissingImageClearsTheCachedSource()
    {
        var disabled = new SponsorBackgroundImageState(
            "C:\\background.png",
            IsEnabled: false,
            IsSponsorUnlocked: true,
            FileExists: true);
        var missing = new SponsorBackgroundImageState(
            "C:\\background.png",
            IsEnabled: true,
            IsSponsorUnlocked: true,
            FileExists: false);

        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldClear(disabled));
        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldClear(missing));
        Assert.IsFalse(SponsorBackgroundImageCachePolicy.ShouldReload(
            disabled,
            disabled,
            hasCachedImage: true,
            forceReload: true));
    }

    [TestMethod]
    public void OnlyLatestActiveLoadMayUpdateTheCache()
    {
        Assert.IsTrue(SponsorBackgroundImageCachePolicy.IsCurrentLoad(
            requestVersion: 3,
            currentVersion: 3,
            isDisposed: false));
        Assert.IsFalse(SponsorBackgroundImageCachePolicy.IsCurrentLoad(
            requestVersion: 2,
            currentVersion: 3,
            isDisposed: false));
        Assert.IsFalse(SponsorBackgroundImageCachePolicy.IsCurrentLoad(
            requestVersion: 3,
            currentVersion: 3,
            isDisposed: true));
    }
}
