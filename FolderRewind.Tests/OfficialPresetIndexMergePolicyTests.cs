using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class OfficialPresetIndexMergePolicyTests
{
    [TestMethod]
    public void V2WinsWhenLegacyItemHasSameShareId()
    {
        var merged = OfficialPresetIndexMergePolicy.MergeV2First(
            new[] { new Item("same", "v2"), new Item("v2-only", "v2") },
            new[] { new Item("same", "v1"), new Item("v1-only", "v1") },
            item => item.ShareId);

        Assert.HasCount(3, merged);
        Assert.AreEqual("v2", merged.Single(item => item.ShareId == "same").Source);
        CollectionAssert.AreEqual(
            new[] { "same", "v2-only", "v1-only" },
            merged.Select(item => item.ShareId).ToArray());
    }

    [TestMethod]
    public void IdentityComparisonIsCaseInsensitive()
    {
        var merged = OfficialPresetIndexMergePolicy.MergeV2First(
            new[] { new Item("ABC", "v2") },
            new[] { new Item("abc", "v1") },
            item => item.ShareId);

        Assert.HasCount(1, merged);
        Assert.AreEqual("v2", merged[0].Source);
    }

    private sealed record Item(string ShareId, string Source);
}
