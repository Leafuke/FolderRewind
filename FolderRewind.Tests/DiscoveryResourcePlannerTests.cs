using FolderRewind.Models;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiscoveryResourcePlannerTests
{
    [TestMethod]
    public void SameRootFileRulesBecomeOneIncludeSourceAndKeepResourceIds()
    {
        var resources = new[]
        {
            Resource("save", "C:\\Game\\Data", "Saves/**/*.sav"),
            Resource("config", "C:\\Game\\Data", "Config/*.json"),
            Resource("registry", string.Empty, null, BackupResourceKind.Registry, BackupResourceSupportState.UnsupportedRegistry)
        };

        var plans = DiscoveryResourcePlanner.CreatePlans(resources);

        Assert.HasCount(1, plans);
        Assert.AreEqual("Data", plans[0].DisplayName);
        Assert.AreEqual(BackupSourceScopeMode.Include, plans[0].ScopeMode);
        CollectionAssert.AreEquivalent(new[] { "Saves/**/*.sav", "Config/*.json" }, plans[0].IncludePatterns.ToArray());
        CollectionAssert.AreEquivalent(new[] { "save", "config" }, plans[0].ResourceIds.ToArray());
    }

    [TestMethod]
    public void ManagedSourceNameUsesFixedRootLeafInsteadOfProviderLabel()
    {
        var resources = new[]
        {
            new BackupResourceCandidate
            {
                ResourceId = "save",
                ProviderId = "ludusavi",
                DisplayName = "Monument Valley 2: Panoramic Edition SAVE",
                Kind = BackupResourceKind.FileSet,
                SupportState = BackupResourceSupportState.Supported,
                FixedRoot = "C:\\Users\\admin\\AppData\\LocalLow\\ustwo games\\Monument Valley 2\\CloudSave",
                IncludePatterns = new[] { "*/*.sav" },
                IsSelectedByDefault = true
            }
        };

        var plans = DiscoveryResourcePlanner.CreatePlans(resources);

        Assert.HasCount(1, plans);
        Assert.AreEqual("CloudSave", plans[0].DisplayName);
    }

    [TestMethod]
    public void WholeDirectoryResourceWinsOverNarrowRulesAtSameRoot()
    {
        var resources = new[]
        {
            Resource("save", "C:\\Game\\Data", "Saves/*.sav"),
            Resource("all", "C:\\Game\\Data", null, BackupResourceKind.Directory)
        };

        var plans = DiscoveryResourcePlanner.CreatePlans(resources);

        Assert.HasCount(1, plans);
        Assert.AreEqual(BackupSourceScopeMode.All, plans[0].ScopeMode);
        Assert.IsEmpty(plans[0].IncludePatterns);
    }

    [TestMethod]
    public void ExplicitSelectionCanIncludeLowConfidenceUncheckedResourceButNotSuppressedOne()
    {
        var selectable = Resource("manual", "C:\\Game\\Manual", "*.sav");
        selectable = Copy(selectable, selected: false);
        var suppressed = Resource("suppressed", "C:\\Game\\Other", "*.sav");
        suppressed.SuppressedByProviderId = "specialized";

        var plans = DiscoveryResourcePlanner.CreatePlans(
            new[] { selectable, suppressed },
            new HashSet<string>(new[] { "manual", "suppressed" }, StringComparer.OrdinalIgnoreCase));

        Assert.HasCount(1, plans);
        Assert.AreEqual("manual", plans[0].ResourceIds[0]);
    }

    private static BackupResourceCandidate Resource(
        string id,
        string root,
        string? pattern,
        BackupResourceKind kind = BackupResourceKind.FileSet,
        BackupResourceSupportState support = BackupResourceSupportState.Supported) => new()
    {
        ResourceId = id,
        ProviderId = "test",
        DisplayName = id,
        Kind = kind,
        SupportState = support,
        FixedRoot = root,
        IncludePatterns = pattern == null ? Array.Empty<string>() : new[] { pattern },
        IsSelectedByDefault = true
    };

    private static BackupResourceCandidate Copy(BackupResourceCandidate source, bool selected) => new()
    {
        ResourceId = source.ResourceId,
        ProviderId = source.ProviderId,
        DisplayName = source.DisplayName,
        Kind = source.Kind,
        SupportState = source.SupportState,
        FixedRoot = source.FixedRoot,
        IncludePatterns = source.IncludePatterns,
        IsSelectedByDefault = selected
    };
}
