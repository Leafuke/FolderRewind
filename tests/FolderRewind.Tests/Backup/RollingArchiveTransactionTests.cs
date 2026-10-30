using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RollingArchiveTransactionTests
{
    [TestMethod]
    public async Task CancellationDuringFactoryCopyRemovesOnlyPartialStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindRollingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseline = Path.Combine(root, "baseline.7z");
            using (var file = File.Create(baseline)) file.SetLength(32 * 1024 * 1024);
            using var cancellation = new CancellationTokenSource();
            var creating = RollingArchiveTransaction.CreateAsync(baseline, root, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => creating);
            CollectionAssert.AreEqual(new[] { baseline }, Directory.GetFiles(root));
            Assert.AreEqual(32L * 1024 * 1024, new FileInfo(baseline).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CommitCreatesNewArchiveWithoutChangingBaselineBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindRollingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseline = Path.Combine(root, "baseline.7z");
            var final = Path.Combine(root, "rolling.7z");
            File.WriteAllText(baseline, "immutable baseline");
            var before = File.ReadAllBytes(baseline);
            using var transaction = await RollingArchiveTransaction.CreateAsync(baseline, root);
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
    public async Task DisposeRollsBackOnlyUniqueStagingCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindRollingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseline = Path.Combine(root, "baseline.7z");
            File.WriteAllText(baseline, "baseline");
            string staging;
            using (var transaction = await RollingArchiveTransaction.CreateAsync(baseline, root))
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
