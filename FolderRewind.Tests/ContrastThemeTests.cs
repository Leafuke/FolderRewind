using System.Xml.Linq;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ContrastThemeTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void ContrastSwitchSuspendsAndRestoresSavedPersonalization()
    {
        var normal = PersonalizationThemePolicy.Resolve(5, true, unlocked: true, highContrast: false);
        var contrast = PersonalizationThemePolicy.Resolve(5, true, unlocked: true, highContrast: true);
        Assert.AreEqual(new PersonalizationThemeState(0, false), contrast);
        var image = new SponsorBackgroundImageState("C:\\background.png", normal.BackgroundEnabled, true, true);
        var hidden = image with { IsEnabled = contrast.BackgroundEnabled };
        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldClear(hidden));
        Assert.AreEqual(normal, PersonalizationThemePolicy.Resolve(5, true, true, false));
        Assert.IsTrue(SponsorBackgroundImageCachePolicy.ShouldReload(hidden, image, false, false));
        Assert.AreEqual(new PersonalizationThemeState(0, false), PersonalizationThemePolicy.Resolve(5, true, false, false));
        Assert.IsFalse(PersonalizationThemePolicy.Resolve(5, false, true, false).BackgroundEnabled);
    }

    [TestMethod]
    public void StatusResourcesHaveOpaqueSystemBrushesForEveryContrastKey()
    {
        var doc = Load("Styles/StatusResources.xaml");
        var dictionaries = doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(X + "Key") != null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value);
        var keys = dictionaries["Light"].Elements().Select(e => e.Attribute(X + "Key")!.Value).ToArray();
        Assert.HasCount(10, keys);
        foreach (var theme in new[] { "Default", "Dark", "HighContrast" })
            CollectionAssert.AreEquivalent(keys, dictionaries[theme].Elements().Select(e => e.Attribute(X + "Key")!.Value).ToArray());
        foreach (var brush in dictionaries["HighContrast"].Elements())
        {
            Assert.AreEqual("StaticResource", brush.Name.LocalName);
            Assert.IsTrue(new[] { "SystemColorWindowBrush", "SystemColorWindowTextBrush" }.Contains(brush.Attribute("ResourceKey")!.Value));
            Assert.IsNull(brush.Attribute("Opacity"));
            Assert.IsNull(brush.Attribute("Color"));
        }
        Assert.IsNull(Load("App.xaml").Root!.Attribute("HighContrastAdjustment"));
    }

    [TestMethod]
    public void StatusPresentersKeepThemeReferencesAndNonColorDistinctions()
    {
        var log = Load("Controls/LogLevelPresenter.xaml");
        foreach (var level in new[] { "Info", "Warning", "Error", "Debug" })
        {
            var state = log.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == level);
            Assert.AreEqual(3, state.Descendants().Count(e => e.Name.LocalName == "Setter"));
            Assert.IsTrue(state.Descendants().Where(e => e.Name.LocalName == "Setter")
                .All(e => e.Attribute("Value")!.Value.StartsWith("{ThemeResource ")));
        }
        Assert.IsTrue(log.Descendants().Any(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute(X + "Name") == "LevelText"));
        var favorite = Load("Controls/FavoriteStatusPresenter.xaml");
        var glyphs = favorite.Descendants().Where(e => (string?)e.Attribute("Target") == "StarIcon.Glyph")
            .Select(e => e.Attribute("Value")!.Value).ToArray();
        Assert.AreEqual(2, glyphs.Distinct().Count());
        Assert.IsTrue(Load("Views/FolderManagerPage.xaml").Descendants().Any(e => e.Name.LocalName == "FavoriteStatusPresenter"));
        Assert.IsTrue(Load("Views/LogPage.xaml").Descendants().Any(e => e.Name.LocalName == "LogLevelPresenter"));
    }

    private static XDocument Load(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "FolderRewind.slnx"))) root = root.Parent;
        return XDocument.Load(Path.Combine(root!.FullName, "FolderRewind", path));
    }
}
