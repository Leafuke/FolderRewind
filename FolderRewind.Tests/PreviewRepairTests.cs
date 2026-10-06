using FolderRewind.Services;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PreviewRepairTests
{
    [TestMethod]
    public void EnglishDefaultWinsOverUnrelatedChineseTranslation()
    {
        var values = new Dictionary<string, string> { ["zh-CN"] = "主世界" };
        Assert.AreEqual("Overworld", LocalizedTextSelector.Select(values, "Overworld", "en-US"));
        Assert.AreEqual("主世界", LocalizedTextSelector.Select(values, "Overworld", "zh-CN"));
        Assert.AreEqual("主世界", LocalizedTextSelector.Select(values, "Overworld", "zh-Hans"));
        Assert.AreEqual("Overworld", LocalizedTextSelector.Select(values, "Overworld", "fr-FR"));
        Assert.AreEqual("en-US", LocalizedTextSelector.EffectiveLanguage("fr-FR"));
        Assert.AreEqual("zh-CN", LocalizedTextSelector.EffectiveLanguage("zh-Hans-CN"));
    }
    [TestMethod]
    public void LanguageMatchingUsesAliasesAndDeterministicFallback()
    {
        var values = new Dictionary<string, string> { ["zh-TW"] = "世界", ["en-US"] = "World", ["fr"] = "Monde" };
        Assert.AreEqual("世界", LocalizedTextSelector.Select(values, "Default", "zh-Hant"));
        Assert.AreEqual("Monde", LocalizedTextSelector.Select(values, "Default", "fr-FR"));
        Assert.AreEqual("World", LocalizedTextSelector.Select(values, null, "de-DE"));
        Assert.AreEqual("Fallback", LocalizedTextSelector.Select(null, "Fallback", "en-US"));
    }
    [TestMethod]
    public void CameraStoresLastActiveLayerAndEvictsLeastRecentEntry()
    {
        var store = new PreviewCameraStore();
        var world = new PreviewWorldKey("c", Guid.NewGuid(), "world");
        store.Save(world, new("overworld", 1, 2, 1, 100));
        store.Save(world, new("nether", 3, 4, 2, 120));
        store.Save(world, new("overworld", 5, 6, 4, 200));
        Assert.AreEqual("overworld", store.Get(world)!.LayerId);
        Assert.AreEqual(5d, store.Get(world, "overworld")!.X);
        for (var i = 0; i < 127; i++) store.Save(world, new("layer" + i, 0, 0, 1, null));
        Assert.AreEqual(128, store.Count);
        Assert.IsNull(store.Get(world, "nether"));
        Assert.IsNotNull(store.Get(world, "overworld"));
    }
    [TestMethod]
    public void NavigationDirectionsBoundsAndScaleBarAreConsistent()
    {
        var camera = new SpatialPreviewViewport();
        camera.Pan(1, 0); Assert.AreEqual((64d, 0d), (camera.CenterX, camera.CenterY));
        camera.Pan(0, -1); Assert.AreEqual((64d, -64d), (camera.CenterX, camera.CenterY));
        camera.Pan(-1, 0); camera.Pan(0, 1); Assert.AreEqual((0d, 0d), (camera.CenterX, camera.CenterY));
        var bounds = new SpatialPreviewBounds(-30, -40, 30, 40);
        Assert.IsTrue(SpatialPreviewCoordinates.Contains(bounds, 30, -40));
        Assert.IsFalse(SpatialPreviewCoordinates.Contains(bounds, 31, 0));
        Assert.IsFalse(SpatialPreviewCoordinates.Contains(null, double.NaN, 0));
        var bar = PreviewScaleBar.Calculate(.125);
        Assert.AreEqual(bar.Units * .125, bar.Pixels);
        Assert.IsTrue(bar.Pixels is >= 20 and <= 100);
    }
}
