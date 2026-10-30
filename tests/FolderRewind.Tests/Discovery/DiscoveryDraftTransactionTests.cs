using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiscoveryDraftTransactionTests
{
    [TestMethod]
    public void SaveFailureRollsBackAllInMemoryMutations()
    {
        var values = new List<string> { "existing" };

        var result = DiscoveryDraftTransaction.Execute(
            apply: () =>
            {
                values.Add("config");
                values.Add("source");
            },
            rollback: () =>
            {
                values.Remove("source");
                values.Remove("config");
            },
            save: () => new DiscoveryDraftTransactionResult(false, "disk full"));

        Assert.IsFalse(result.Success);
        CollectionAssert.AreEqual(new[] { "existing" }, values);
    }

    [TestMethod]
    public void SuccessfulBatchSavesExactlyOnceAndKeepsAllMutations()
    {
        var values = new List<string>();
        var saveCount = 0;

        var result = DiscoveryDraftTransaction.Execute(
            apply: () => values.AddRange(new[] { "config", "source" }),
            rollback: values.Clear,
            save: () =>
            {
                saveCount++;
                return new DiscoveryDraftTransactionResult(true, string.Empty);
            });

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, saveCount);
        CollectionAssert.AreEqual(new[] { "config", "source" }, values);
    }
}
