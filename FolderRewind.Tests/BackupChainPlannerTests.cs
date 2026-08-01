using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupChainPlannerTests
{
    [TestMethod]
    public void LocalPolicyPrependsBaseDeduplicatesAndAppendsTarget()
    {
        var full = Item("full", 1, "Full");
        var smart = Item("smart", 2, "Smart");
        var duplicate = Item("SMART", 3, "Smart");
        var target = Item("target", 4, "Smart");

        var plan = BackupChainPlanner.Build(
            new[] { smart, full, duplicate },
            target,
            targetIsIncremental: true,
            LocalOptions());

        Assert.AreEqual(BackupChainPlanStatus.Success, plan.Status);
        CollectionAssert.AreEqual(new[] { full, smart, target }, plan.Items.ToArray());
    }

    [TestMethod]
    public void CloudPolicyKeepsDuplicatesAndDoesNotSynthesizeMissingTarget()
    {
        var full = Item("full", 1, "Full");
        var smart1 = Item("smart", 2, "Smart");
        var smart2 = Item("smart", 3, "Smart");
        var remoteTarget = Item("target", 4, "Smart");

        var plan = BackupChainPlanner.Build(
            new[] { full, smart1, smart2 },
            remoteTarget,
            targetIsIncremental: true,
            CloudOptions());

        Assert.AreEqual(BackupChainPlanStatus.Success, plan.Status);
        CollectionAssert.AreEqual(new[] { full, smart1, smart2 }, plan.Items.ToArray());
    }

    [TestMethod]
    public void FullTargetReturnsOnlyTargetWithoutRequiringCandidates()
    {
        var target = Item("full", 1, "Full");

        var plan = BackupChainPlanner.Build(
            Array.Empty<ChainItem>(),
            target,
            targetIsIncremental: false,
            LocalOptions());

        Assert.AreEqual(BackupChainPlanStatus.Success, plan.Status);
        CollectionAssert.AreEqual(new[] { target }, plan.Items.ToArray());
    }

    [TestMethod]
    public void IncrementalTargetWithoutBaseFullReportsMissingBase()
    {
        var target = Item("smart", 2, "Smart");

        var plan = BackupChainPlanner.Build(
            new[] { target },
            target,
            targetIsIncremental: true,
            LocalOptions());

        Assert.AreEqual(BackupChainPlanStatus.MissingBaseFull, plan.Status);
        Assert.IsEmpty(plan.Items);
    }

    private static BackupChainPlanOptions<ChainItem> LocalOptions()
        => new()
        {
            GetTimestamp = item => item.Timestamp,
            GetIdentity = item => item.Identity,
            IsFull = item => item.Type == "Full",
            IsIncremental = item => item.Type == "Smart",
            SelectBaseFull = candidates => candidates.OrderByDescending(item => item.Timestamp).FirstOrDefault(),
            OrderChain = candidates => candidates.OrderBy(item => item.Timestamp).ThenBy(item => item.Identity),
            IdentityComparer = StringComparer.OrdinalIgnoreCase,
            PrependBaseFull = true,
            IncludeTargetInWindow = false,
            EnsureTargetIncluded = true,
            Deduplicate = true
        };

    private static BackupChainPlanOptions<ChainItem> CloudOptions()
        => new()
        {
            GetTimestamp = item => item.Timestamp,
            GetIdentity = item => item.Identity,
            IsFull = item => item.Type == "Full",
            IsIncremental = item => item.Type == "Smart",
            SelectBaseFull = candidates => candidates.OrderBy(item => item.Timestamp).LastOrDefault(),
            OrderChain = candidates => candidates.OrderBy(item => item.Timestamp).ThenBy(item => item.Identity),
            IdentityComparer = StringComparer.OrdinalIgnoreCase,
            IncludeTargetInWindow = true
        };

    private static ChainItem Item(string identity, int minute, string type)
        => new(identity, new DateTime(2026, 8, 1, 0, minute, 0, DateTimeKind.Utc), type);

    private sealed record ChainItem(string Identity, DateTime Timestamp, string Type);
}
