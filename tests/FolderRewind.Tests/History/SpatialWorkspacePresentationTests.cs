using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialWorkspacePresentationTests
{
    [TestMethod]
    public void AreaSelectionDefaultsToUnresolvedAndRequiresExplicitExistingDecisionScope()
    {
        WithIndex(index =>
        {
            index.Put([Row("unresolved", 0, []) with { Status = "Unresolved" }, Row("automatic", 16, []) with { Status = "Automatic" },
                Row("manual", 32, []) with { Status = "Resolved" }, Row("locked", 48, []) with { Status = "Locked", CanSelect = false }]);
            var query = new SpatialWorkspaceQuery("world", "layer", Group: false);
            var area = new SpatialPreviewBounds(0, 0, 64, 16);
            var summary = index.SelectArea(query, area, false, false);
            CollectionAssert.AreEqual(new[] { "unresolved" }, index.Selected());
            Assert.AreEqual(new SpatialAreaSelectionSummary(1, 2, 1), summary);
            index.SelectArea(query, area, false, false, onlyUnresolved: false);
            CollectionAssert.AreEquivalent(new[] { "unresolved", "automatic", "manual" }, index.Selected());
            index.SelectArea(query, area, false, true);
            Assert.HasCount(0, index.Selected());
            index.SelectArea(query with { Search = "automatic" }, area, false, false);
            Assert.HasCount(0, index.Selected(), "A filter cannot silently opt into overwriting automatic decisions.");
        });
    }
    [TestMethod]
    public void ZoomKeepsTheWorldPointUnderThePointerAndRoundTripsAcrossNegativeCoordinates()
    {
        var camera = new SpatialPreviewViewport { CenterX = -513, CenterY = 17 };
        camera.SetScale(.25);
        var before = camera.ToWorld(137, 82, 811, 619);
        camera.ZoomAt(2, 137, 82, 811, 619);
        var after = camera.ToWorld(137, 82, 811, 619);
        Assert.AreEqual(before.X, after.X, 1e-8); Assert.AreEqual(before.Y, after.Y, 1e-8);
        var screen = camera.ToScreen(after.X, after.Y, 811, 619);
        Assert.AreEqual(137, screen.X, 1e-8); Assert.AreEqual(82, screen.Y, 1e-8);
        camera.Fit(new(-1024, -512, 512, 1024), 900, 600);
        var min = camera.ToScreen(-1024, -512, 900, 600); var max = camera.ToScreen(512, 1024, 900, 600);
        Assert.IsTrue(min.X >= 23 && min.Y >= 23 && max.X <= 877 && max.Y <= 577);
    }

    [TestMethod]
    public void ClassificationGroupingAndNumericOrderMatchMapCountsWithoutSelectingMergeItems()
    {
        WithIndex(index =>
        {
            index.Put([Row("blocks", -160, ["blocks"]), Row("entity", -160, ["entities"]),
                Row("closer", -16, ["blocks"]), Row("other-world", 0, ["blocks"], "elsewhere"),
                Row("unknown", 16, []), new("file", "file", "detail", "Modified", false, []) { SourcePaths = ["data/file.dat"] }]);
            var query = new SpatialWorkspaceQuery("world", "layer", Categories: ["blocks"]);
            Assert.AreEqual(3, index.Count(query));
            Assert.AreEqual(-160d, index.Query(query, 0)[0].Locations[0].Bounds!.MinX);
            var all = query with { Categories = null };
            var row = index.Query(all, 0)[0];
            CollectionAssert.AreEquivalent(new[] { "blocks", "entity" }, row.MemberIds);
            Assert.HasCount(0, index.Selected());
            Assert.AreEqual(3, index.Overlays(all, new(-512, -512, 512, 512), 1, 16).Sum(o => o.Count));
            Assert.AreEqual(1, index.Count(query with { Files = true, Categories = null }));
            index.ToggleItem("blocks", true);
            CollectionAssert.AreEqual(new[] { "blocks" }, index.Selected());
        });
    }

    [TestMethod]
    public void DenseMarkersUseStableWorldCellsAndCountLocationsRatherThanComponents()
    {
        WithIndex(index =>
        {
            index.Put(Enumerable.Range(-600, 1201).SelectMany(i => new[] { Row("b" + i, i * 16, ["blocks"]), Row("e" + i, i * 16, ["entities"]) }));
            var query = new SpatialWorkspaceQuery("world", "layer");
            var before = index.Overlays(query, new(-20000, -10000, 20000, 10000), .02, 16);
            var after = index.Overlays(query, new(-19900, -10000, 20100, 10000), .02, 16);
            Assert.IsTrue(before.All(o => o.Aggregate));
            Assert.AreEqual(1201, before.Sum(o => o.Count));
            CollectionAssert.AreEquivalent(before.Select(o => o.Bounds).ToArray(), after.Select(o => o.Bounds).ToArray());
            Assert.IsTrue(before.Any(o => o.Bounds.MinX < 0 && o.Bounds.MaxX <= 0));
        });
    }
    private static SpatialWorkspaceRow Row(string id, int x, string[] categories, string world = "world")
        => new(id, id, "details", "Modified", true, [new(world, "layer", new(x, 0, x + 16, 16))]) { Components = categories };
    private static void WithIndex(Action<SpatialWorkspaceIndex> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindSpatialPresentation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db")); test(index); }
        finally { Directory.Delete(root, true); }
    }
}
