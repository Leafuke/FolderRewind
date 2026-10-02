using FolderRewind.Models;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiscoveryPathPolicyTests
{
    [TestMethod]
    public void NormalizationPreservesDriveAndShareRootsAndComparisonIgnoresCase()
    {
        Assert.IsTrue(DiscoveryPathPolicy.TryNormalizeAbsolutePath("C:/", out var drive));
        Assert.AreEqual(@"C:\", drive);
        Assert.IsTrue(DiscoveryPathPolicy.TryNormalizeAbsolutePath(@"\\server\share", out var share));
        Assert.AreEqual(@"\\server\share\", share);
        Assert.IsTrue(DiscoveryPathPolicy.Equals(@"\\SERVER\share\", @"\\server\SHARE"));
        Assert.IsTrue(DiscoveryPathPolicy.Equals(@"C:\Game\Data\", "c:/game/data"));
    }

    [TestMethod]
    public void InvalidPathsNeverTurnIntoCurrentDirectoryOrCompareEqual()
    {
        foreach (var path in new string?[] { null, "", "  ", "relative", "C:", @"C:folder", "C:/bad\0path", "C:/a*b", "C:/a?b", "C:/file:stream", @"\\server" })
        {
            Assert.IsFalse(DiscoveryPathPolicy.TryNormalizeAbsolutePath(path, out var normalized), path);
            Assert.AreEqual(string.Empty, normalized);
            Assert.IsFalse(DiscoveryPathPolicy.Equals(path, path));
        }
    }

    [TestMethod]
    public void PlannerSkipsInvalidCandidatesAndCombinesEquivalentValidRoots()
    {
        BackupResourceCandidate Resource(string id, string path) => new()
        {
            ResourceId = id, ProviderId = "test", DisplayName = id, FixedRoot = path,
            Kind = BackupResourceKind.Directory, IsSelectedByDefault = true
        };
        var plans = DiscoveryResourcePlanner.CreatePlans(new[]
        {
            Resource("one", @"C:\Data\"), Resource("two", "c:/DATA"),
            Resource("relative", "Data"), Resource("bad", "C:/bad\0path")
        });
        Assert.HasCount(1, plans);
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, plans[0].ResourceIds.ToArray());
        Assert.AreEqual(@"C:\Data", plans[0].FixedRoot);
    }

    [TestMethod]
    public void ExistingIdentityEncodingRetainsItsPublishedRootRepresentation()
    {
        // These strings are embedded in existing IDs; changing them would create new resources.
        Assert.AreEqual("C:", DiscoveryIdentityPathV1.Encode(@"C:\"));
        Assert.AreEqual(@"C:\Game\Data", DiscoveryIdentityPathV1.Encode("C:/Game/Data/"));
        Assert.AreEqual(@"\\server\share", DiscoveryIdentityPathV1.Encode(@"\\server\share\"));
    }

    [TestMethod]
    public void LegacyReviewedVolumeRootDoesNotBecomeAnAddedOrRemovedSource()
    {
        var previous = new ReviewedDiscoveryBaseline();
        previous.Sources.Add(new ReviewedDiscoverySource { NormalizedRootPath = "C:", Mode = BackupSourceScopeMode.All });
        var next = new ReviewedDiscoveryBaseline();
        next.Sources.Add(new ReviewedDiscoverySource { NormalizedRootPath = @"C:\", Mode = BackupSourceScopeMode.All });
        Assert.IsEmpty(DiscoveryThreeWayReviewService.Review(previous, next.Sources, next));
    }

    [TestMethod]
    public void ScannerRejectsBadPathsWithDiagnosticsAndDeduplicatesValidInstallations()
    {
        var discovery = new LauncherInstallationDiscoveryService(new[] { new FakeScanner() });
        var result = discovery.Scan(new Dictionary<GameStore, IReadOnlyList<string>>
        {
            [GameStore.Standalone] = new[] { "relative" }
        }, [], CancellationToken.None);
        Assert.HasCount(1, result.Installations);
        Assert.HasCount(2, result.Diagnostics.Where(item => item.Code == "invalid_discovery_path").ToArray());
    }

    private sealed class FakeScanner : ILauncherInstallationScanner
    {
        public GameStore Store => GameStore.Standalone;
        public LauncherInstallationScanResult Scan(IEnumerable<string> roots, CancellationToken cancellationToken) => new()
        {
            Installations = new[]
            {
                new DetectedGameInstallation { Store = Store, StoreGameId = "one", BasePath = @"C:\Game\" },
                new DetectedGameInstallation { Store = Store, StoreGameId = "one", BasePath = "c:/game" },
                new DetectedGameInstallation { Store = Store, StoreGameId = "bad", BasePath = "relative" }
            }
        };
    }
}
