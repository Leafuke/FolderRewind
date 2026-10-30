using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BundledMineRewindPackageTests
{
    [TestMethod]
    public void PresetSelectsExactBundleWhenOlderPackagesRemain()
    {
        Fixture(root =>
        {
            File.WriteAllBytes(Path.Combine(root, "Assets", "Plugins", "MineRewind-1.9.19.frplugin"), [1]);
            var expected = Path.Combine(root, "Assets", "Plugins", "MineRewind-1.9.21.frplugin");
            File.WriteAllBytes(expected, [2]); Preset(root, "Assets/Plugins/MineRewind-1.9.21.frplugin");
            Assert.AreEqual(expected, BundledMineRewindPackage.Resolve(root));
        });
    }
    [TestMethod]
    public void MissingSelectedBundleNeverSilentlyFallsBackAndTraversalIsRejected()
    {
        Fixture(root =>
        {
            File.WriteAllBytes(Path.Combine(root, "MineRewind-old.frplugin"), [1]);
            Preset(root, "Assets/Plugins/MineRewind-missing.frplugin");
            Assert.ThrowsExactly<FileNotFoundException>(() => BundledMineRewindPackage.Resolve(root));
            Preset(root, "Assets/../MineRewind-old.frplugin");
            Assert.ThrowsExactly<InvalidDataException>(() => BundledMineRewindPackage.Resolve(root));
        });
    }
    [TestMethod]
    public void FlattenedPackageLayoutStillUsesThePresetIdentity()
    {
        Fixture(root =>
        {
            var expected = Path.Combine(root, "MineRewind-1.9.21.frplugin");
            File.WriteAllBytes(expected, [1]); File.WriteAllBytes(Path.Combine(root, "MineRewind-old.frplugin"), [2]);
            Preset(root, "Assets/Plugins/MineRewind-1.9.21.frplugin");
            Assert.AreEqual(expected, BundledMineRewindPackage.Resolve(root));
        });
    }
    private static void Preset(string root, string path)
        => File.WriteAllText(Path.Combine(root, "Assets", "PluginPresets", "minecraft-enhanced-experience.v1.json"),
            JsonSerializer.Serialize(new { actions = new[] { new { id = "install-minerewind", packagePath = path } } }));
    private static void Fixture(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Plugins")); Directory.CreateDirectory(Path.Combine(root, "Assets", "PluginPresets"));
        try { test(root); } finally { Directory.Delete(root, true); }
    }
}
