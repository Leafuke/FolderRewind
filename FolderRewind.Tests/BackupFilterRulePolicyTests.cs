using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupFilterRulePolicyTests
{
    [TestMethod]
    public void AddDistinctTrimsAndPreservesFirstInsertionOrder()
    {
        var rules = new List<string> { "world/region", "playerdata" };

        BackupFilterRulePolicy.AddDistinct(rules, "  DIM-1/region  ");
        BackupFilterRulePolicy.AddDistinct(rules, " WORLD/REGION ");
        BackupFilterRulePolicy.AddDistinct(rules, "   ");

        CollectionAssert.AreEqual(
            new[] { "world/region", "playerdata", "DIM-1/region" },
            rules);
    }
}
