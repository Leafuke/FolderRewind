using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using System.Collections.ObjectModel;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiscoveryThreeWayReviewServiceTests
{
    [TestMethod]
    public void AddedSourceUsesDiscoveryDefaultButRemovedSourceIsOptIn()
    {
        var oldBaseline = Baseline(Source("C:/old", "old/**"));
        var current = new[] { Source("C:/old", "old/**") };
        var next = Baseline(Source("C:/new", "new/**"));

        var changes = DiscoveryThreeWayReviewService.Review(
            oldBaseline,
            current,
            next,
            new HashSet<string>(new[] { DiscoveryResourcePlanner.NormalizePath("C:/new") }, StringComparer.OrdinalIgnoreCase));

        Assert.HasCount(2, changes);
        Assert.IsTrue(changes.Single(change => change.Kind == DiscoverySourceChangeKind.Added).IsSelected);
        Assert.IsFalse(changes.Single(change => change.Kind == DiscoverySourceChangeKind.Removed).IsSelected);
    }

    [TestMethod]
    public void UnchangedUpstreamWithUserEditIsInformational()
    {
        var baseline = Baseline(Source("C:/game", "saves/**"));

        var changes = DiscoveryThreeWayReviewService.Review(
            baseline,
            new[] { Source("C:/game", "custom/**") },
            baseline);

        var change = changes.Single();
        Assert.AreEqual(DiscoverySourceChangeKind.UserModified, change.Kind);
        Assert.IsFalse(change.IsActionable);
        Assert.IsFalse(change.IsSelected);
    }

    [TestMethod]
    public void UserAndUpstreamEditingSameSourceCreatesConflict()
    {
        var oldBaseline = Baseline(Source("C:/game", "saves/**"));
        var next = Baseline(Source("C:/game", "profiles/**"));

        var change = DiscoveryThreeWayReviewService.Review(
            oldBaseline,
            new[] { Source("C:/game", "custom/**") },
            next).Single();

        Assert.AreEqual(DiscoverySourceChangeKind.Conflict, change.Kind);
        Assert.IsFalse(change.IsSelected);
    }

    [TestMethod]
    public void ShrinkIsNotSelectedButExpansionIsSelected()
    {
        var root = DiscoveryResourcePlanner.NormalizePath("C:/game");
        var shrink = DiscoveryThreeWayReviewService.Review(
            Baseline(Source("C:/game", "a/**", "b/**")),
            new[] { Source("C:/game", "a/**", "b/**") },
            Baseline(Source("C:/game", "a/**")),
            new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase)).Single();
        var expansion = DiscoveryThreeWayReviewService.Review(
            Baseline(Source("C:/game", "a/**")),
            new[] { Source("C:/game", "a/**") },
            Baseline(Source("C:/game", "a/**", "b/**")),
            new HashSet<string>(new[] { root }, StringComparer.OrdinalIgnoreCase)).Single();

        Assert.IsFalse(shrink.IsSelected);
        Assert.IsTrue(expansion.IsSelected);
    }

    [TestMethod]
    public void RejectedChangeBecomesOverrideUntilUpstreamChangesAgain()
    {
        var oldBaseline = Baseline(Source("C:/game", "old/**"));
        var next = Baseline(Source("C:/game", "new/**"));
        var current = new[] { Source("C:/game", "old/**") };
        var completed = DiscoveryThreeWayReviewService.CompleteReview(
            next,
            current,
            oldBaseline.Sources.Select(source => source.NormalizedRootPath));

        Assert.IsEmpty(DiscoveryThreeWayReviewService.Review(completed, current, next));

        var later = Baseline(Source("C:/game", "later/**"));
        var change = DiscoveryThreeWayReviewService.Review(completed, current, later).Single();
        Assert.AreEqual(DiscoverySourceChangeKind.Conflict, change.Kind);
    }

    [TestMethod]
    public void ManualSourceOutsideBothBaselinesIsNeverConsidered()
    {
        var changes = DiscoveryThreeWayReviewService.Review(
            new ReviewedDiscoveryBaseline(),
            new[] { Source("C:/manual", "**") },
            new ReviewedDiscoveryBaseline());

        Assert.IsEmpty(changes);
    }

    private static ReviewedDiscoveryBaseline Baseline(params ReviewedDiscoverySource[] sources) => new()
    {
        Sources = new ObservableCollection<ReviewedDiscoverySource>(sources)
    };

    private static ReviewedDiscoverySource Source(string path, params string[] patterns) => new()
    {
        NormalizedRootPath = DiscoveryResourcePlanner.NormalizePath(path),
        Mode = BackupSourceScopeMode.Include,
        IncludePatterns = new ObservableCollection<string>(patterns)
    };
}
