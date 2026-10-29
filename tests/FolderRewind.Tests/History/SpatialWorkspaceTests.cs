using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialWorkspaceTests
{
    [TestMethod]
    public void WideViewportCoarsensTilesWithinTheSharedPanelBudget()
    {
        var camera = new SpatialPreviewViewport();
        var tiles = camera.Tiles(7680, 4320, 128).ToArray();
        Assert.IsLessThanOrEqualTo(128, tiles.Length);
        Assert.IsTrue(tiles.Any(t => t.OriginX <= 0 && t.OriginY <= 0 && t.OriginX + 256d * t.UnitsPerPixel > 0 && t.OriginY + 256d * t.UnitsPerPixel > 0));
        Assert.IsTrue(tiles.All(t => t.UnitsPerPixel > 1));
    }

    [TestMethod]
    public void RemovingOneRegionPreservesTheRestOfAnExistingRectangle()
    {
        var parts = SpatialSelectionGeometry.Subtract(new(-512, -512, 1024, 1024), new(0, 0, 512, 512)).ToArray();
        Assert.HasCount(4, parts);
        Assert.AreEqual(8 * 512d * 512, parts.Sum(b => (b.MaxX - b.MinX) * (b.MaxY - b.MinY)));
        Assert.IsFalse(parts.Any(b => b.MinX < 512 && b.MaxX > 0 && b.MinY < 512 && b.MaxY > 0));
    }

    [TestMethod]
    public void SpatialSelectionIncludesOffPageRowsAndRetainsLockedItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindSpatialTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db"));
            index.Put(Enumerable.Range(0, 1200).Select(i => new SpatialWorkspaceRow("row-" + i, "row-" + i, "", i == 5 ? "Locked" : "Automatic", i != 5,
                [new("world", "layer", new(i * 16d, 0, (i + 1d) * 16, 16))])));
            index.SelectArea("world", "layer", new(0, 0, 1200 * 16, 16), false, false, "");
            Assert.HasCount(1199, index.Selected());
            Assert.IsFalse(index.Selected().Contains("row-5"));
            Assert.HasCount(100, index.Query("", "", 0));
            Assert.AreEqual(1200, index.Overlays("world", "layer", new(0, 0, 1200 * 16, 16)).Sum(o => o.Count));
            index.SelectArea("world", "layer", new(16, 0, 32, 16), true, true, "");
            Assert.HasCount(1198, index.Selected());
            Assert.IsFalse(index.Selected().Contains("row-1"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ScopeIntersectionNeverWidensSourceCeiling()
    {
        var rules = SourceScopeIntersection.Intersect(["world/**"], ["world/region/r.0.0.mca", "world/entities/**", "other/region/r.0.0.mca"], default);
        CollectionAssert.AreEquivalent(new[] { "world/region/r.0.0.mca", "world/entities/**" }, rules.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => SourceScopeIntersection.Intersect(["other/**"], ["world/region/r.0.0.mca"], default));
        Assert.ThrowsExactly<InvalidDataException>(() => SourceScopeIntersection.Intersect(["**/*.mca"], ["world/*"], default));
    }
}
