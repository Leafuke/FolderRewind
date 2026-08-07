using FolderRewind.Models;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LauncherInstallationScannerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SteamScannerReadsLibraryFoldersAndAppManifests()
    {
        var primaryRoot = Path.Combine(_root, "Steam");
        var primaryApps = Path.Combine(primaryRoot, "steamapps");
        var secondaryRoot = Path.Combine(_root, "SteamLibrary");
        var secondaryApps = Path.Combine(secondaryRoot, "steamapps");
        Directory.CreateDirectory(primaryApps);
        Directory.CreateDirectory(Path.Combine(secondaryApps, "common", "Hades"));
        Directory.CreateDirectory(Path.Combine(primaryRoot, "userdata", "76561198000000000"));
        await File.WriteAllTextAsync(
            Path.Combine(primaryApps, "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path" "{{secondaryRoot.Replace("\\", "\\\\")}}"
                }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(secondaryApps, "appmanifest_1145360.acf"),
            """
            "AppState"
            {
                "appid" "1145360"
                "name" "Hades"
                "installdir" "Hades"
            }
            """);

        var installations = new SteamInstallationScanner().Scan(new[] { primaryRoot });

        Assert.HasCount(1, installations);
        Assert.AreEqual("1145360", installations[0].StoreGameId);
        Assert.AreEqual("Hades", installations[0].DisplayName);
        CollectionAssert.Contains(installations[0].StoreUserIds.ToList(), "76561198000000000");
    }

    [TestMethod]
    public async Task EpicScannerReadsItemManifest()
    {
        var manifests = Path.Combine(_root, "Manifests");
        var install = Path.Combine(_root, "Games", "Example");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(install);
        await File.WriteAllTextAsync(
            Path.Combine(manifests, "example.item"),
            $$"""
            {
              "InstallLocation": "{{install.Replace("\\", "\\\\")}}",
              "CatalogItemId": "catalog-id",
              "DisplayName": "Example Game"
            }
            """);

        var installations = new EpicInstallationScanner().Scan(new[] { manifests });

        Assert.HasCount(1, installations);
        Assert.AreEqual(GameStore.Epic, installations[0].Store);
        Assert.AreEqual("catalog-id", installations[0].StoreGameId);
        Assert.AreEqual("Example Game", installations[0].DisplayName);
    }

    [TestMethod]
    public void GogCustomRootProducesNameBasedFallbackInstallation()
    {
        var game = Path.Combine(_root, "GOG", "Baldurs Gate");
        Directory.CreateDirectory(game);

        var installations = new GogInstallationScanner().Scan(new[] { Path.GetDirectoryName(game)! });

        Assert.IsTrue(installations.Any(item =>
            item.Store == GameStore.Gog
            && item.DisplayName == "Baldurs Gate"
            && string.Equals(item.InstallPath, game, StringComparison.OrdinalIgnoreCase)));
    }
}
