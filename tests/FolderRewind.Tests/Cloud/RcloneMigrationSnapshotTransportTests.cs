using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RcloneMigrationSnapshotTransportTests
{
    [TestMethod]
    public async Task RealRclonePublishesOnlyVerifiedSnapshotsAndRejectsCorruptedDownloads()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE to exercise real snapshot transport.");
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindMigrationCloud", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "rclone.conf"); File.WriteAllText(config, "[snapshots]\ntype = local\n");
            var source = Path.Combine(root, "source.frhistory"); await File.WriteAllTextAsync(source, "verified migration bytes");
            using var context = new RcloneExecutionContext(executable, config, root, "snapshots:repository", Path.Combine(root, "tasks"));
            var transport = new RcloneMigrationSnapshotTransport(context);
            Assert.IsEmpty(await transport.ListAsync());
            var snapshot = await transport.UploadAsync(source); var snapshots = await transport.ListAsync(); Assert.HasCount(1, snapshots);
            Assert.AreEqual(snapshot, snapshots.Single());
            var output = Path.Combine(root, "download.frhistory"); await transport.DownloadAsync(snapshot, output);
            CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(output));
            var remote = Path.Combine(root, "repository", "_folderrewind", "migration-exports");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "repository", "_folderrewind", "history")));
            await File.WriteAllTextAsync(Path.Combine(remote, snapshot.FileName), "damaged remote payload");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transport.DownloadAsync(snapshot, output));
            Assert.AreEqual("verified migration bytes", await File.ReadAllTextAsync(output));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => transport.UploadAsync(source, cancel.Token));
            Assert.HasCount(1, await transport.ListAsync());
        }
        finally { Directory.Delete(root, true); }
    }
}
