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
        const string steamId64 = "76561198000000000";
        Assert.IsTrue(SteamInstallationScanner.TryConvertSteamId64(steamId64, out var accountId));
        Directory.CreateDirectory(Path.Combine(primaryRoot, "userdata", accountId));
        Directory.CreateDirectory(Path.Combine(primaryRoot, "userdata", "12345"));
        Directory.CreateDirectory(Path.Combine(primaryRoot, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(primaryRoot, "config", "loginusers.vdf"),
            $$"""
            "users"
            {
                "{{steamId64}}"
                {
                    "MostRecent" "1"
                    "Timestamp" "100"
                }
            }
            """);
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

        var result = new SteamInstallationScanner().Scan(new[] { primaryRoot }, CancellationToken.None);
        var installations = result.Installations;

        Assert.HasCount(1, installations);
        Assert.AreEqual("1145360", installations[0].StoreGameId);
        Assert.AreEqual("Hades", installations[0].DisplayName);
        Assert.AreEqual(Path.GetFullPath(secondaryRoot), Path.GetFullPath(installations[0].RootPath));
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(secondaryApps, "common", "Hades")),
            Path.GetFullPath(installations[0].BasePath));
        Assert.AreEqual("Hades", installations[0].InstalledGameName);
        CollectionAssert.Contains(installations[0].StoreUserIds.ToList(), accountId);
        CollectionAssert.Contains(installations[0].StoreUserIds.ToList(), "12345");
        Assert.AreEqual(accountId, installations[0].ActiveStoreUserId);
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

        var installations = new EpicInstallationScanner()
            .Scan(new[] { manifests }, CancellationToken.None)
            .Installations;

        Assert.HasCount(1, installations);
        Assert.AreEqual(GameStore.Epic, installations[0].Store);
        Assert.AreEqual("catalog-id", installations[0].StoreGameId);
        Assert.AreEqual("Example Game", installations[0].DisplayName);
        Assert.AreEqual(Path.GetDirectoryName(install), installations[0].RootPath);
        Assert.AreEqual(install, installations[0].BasePath);
        Assert.AreEqual("Example", installations[0].InstalledGameName);
    }

    [TestMethod]
    public void GogCustomRootProducesNameBasedFallbackInstallation()
    {
        var game = Path.Combine(_root, "GOG", "Baldurs Gate");
        Directory.CreateDirectory(game);

        var installations = new GogInstallationScanner()
            .Scan(new[] { Path.GetDirectoryName(game)! }, CancellationToken.None)
            .Installations;

        Assert.IsTrue(installations.Any(item =>
            item.Store == GameStore.Gog
            && item.DisplayName == "Baldurs Gate"
            && string.Equals(item.BasePath, game, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ScannerHonorsCancellationBeforeEnumeratingRoots()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new SteamInstallationScanner().Scan(new[] { _root }, source.Token));
    }

    [TestMethod]
    public async Task EpicScannerIsolatesMalformedManifestAndReportsDiagnostic()
    {
        var manifests = Path.Combine(_root, "Manifests");
        var install = Path.Combine(_root, "Games", "Valid");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(install);
        await File.WriteAllTextAsync(Path.Combine(manifests, "broken.item"), "not json");
        await File.WriteAllTextAsync(
            Path.Combine(manifests, "valid.item"),
            $$"""{"InstallLocation":"{{install.Replace("\\", "\\\\")}}","DisplayName":"Valid"}""");

        var result = new EpicInstallationScanner().Scan(new[] { manifests }, CancellationToken.None);

        Assert.HasCount(1, result.Installations);
        Assert.HasCount(1, result.Diagnostics);
        Assert.AreEqual("epic", result.Diagnostics[0].ProviderId);
        Assert.AreEqual("format", result.Diagnostics[0].Category);
        Assert.AreEqual(Path.Combine(manifests, "broken.item"), result.Diagnostics[0].RootPath);
    }
}
