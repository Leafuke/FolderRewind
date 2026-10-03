using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RollingArchiveTransactionTests
{
    [TestMethod]
    public void CommitCreatesNewArchiveWithoutChangingBaselineBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindRollingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseline = Path.Combine(root, "baseline.7z");
            var final = Path.Combine(root, "rolling.7z");
            File.WriteAllText(baseline, "immutable baseline");
            var before = File.ReadAllBytes(baseline);
            using var transaction = RollingArchiveTransaction.Create(baseline, root);
            File.AppendAllText(transaction.StagingPath, " + changes");

            transaction.Commit(final);

            CollectionAssert.AreEqual(before, File.ReadAllBytes(baseline));
            Assert.AreEqual("immutable baseline + changes", File.ReadAllText(final));
            Assert.IsFalse(StringComparer.OrdinalIgnoreCase.Equals(baseline, final));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DisposeRollsBackOnlyUniqueStagingCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindRollingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseline = Path.Combine(root, "baseline.7z");
            File.WriteAllText(baseline, "baseline");
            string staging;
            using (var transaction = RollingArchiveTransaction.Create(baseline, root))
            {
                staging = transaction.StagingPath;
                Assert.IsTrue(File.Exists(staging));
            }

            Assert.IsFalse(File.Exists(staging));
            Assert.IsTrue(File.Exists(baseline));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
