using FolderRewind.History.Domain;
using FolderRewind.History.Cloud;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RcloneNativeHistoryTransportTests
{
    private string _root = null!;
    private RcloneExecutionContext _context = null!;
    private RcloneNativeHistoryTransport Transport => new(_context);

    [TestInitialize]
    public void Initialize()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        _root = Path.Combine(Path.GetTempPath(), "NativeSingleFileTransport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[test]\ntype = local\n");
        _context = new(executable, config, _root, "test:repository", Path.Combine(_root, "tasks"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context?.Dispose();
        if (_root is not null && Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task RealMetadataPublicationIsIdempotentAndRejectsConflictingBytes()
    {
        var transport = Transport;
        var config = new HistoryConfigId("config"); var pack = PackId.New();
        byte[] bytes = [1, 2, 3];
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await transport.CreateDescriptorOnceAsync(config, bytes, default);
            await transport.UploadPackOnceAsync(config, pack, bytes, default);
        }
        await Assert.ThrowsAsync<IOException>(() => transport.CreateDescriptorOnceAsync(config, [9, 8, 7, 6], default));
        await Assert.ThrowsAsync<IOException>(() => transport.UploadPackOnceAsync(config, pack, [9, 8, 7, 6], default));
        await Assert.ThrowsAsync<IOException>(() => transport.CreateDescriptorOnceAsync(config, [9, 8, 7], default));
        await Assert.ThrowsAsync<IOException>(() => transport.UploadPackOnceAsync(config, pack, [9, 8, 7], default));
        CollectionAssert.AreEqual(bytes, await transport.ReadDescriptorAsync(config, default));
        CollectionAssert.AreEqual(bytes, await transport.DownloadPackAsync(config, pack, default));
        CollectionAssert.AreEqual(new[] { pack }, (await transport.ListPacksAsync(config, default)).ToArray());

        var manifest = new HistoryReplicaManifest(RepresentationId.New(), ReplicaId.New(), null, "object", 3, new string('a', 64));
        await transport.CommitManifestOnceAsync(manifest, default);
        await transport.CommitManifestOnceAsync(manifest, default);
        await Assert.ThrowsAsync<IOException>(() => transport.CommitManifestOnceAsync(manifest with { Size = 12345 }, default));
        await Assert.ThrowsAsync<IOException>(() => transport.CommitManifestOnceAsync(manifest with { StorageSha256 = new string('b', 64) }, default));
        Assert.AreEqual(manifest, await transport.ReadManifestAsync(manifest.ReplicaId, default));
    }

    [TestMethod]
    public async Task ExistingArtifactConflictFailsVerificationWithoutReplacingRemoteBytes()
    {
        var input = Path.Combine(_root, "artifact");
        byte[] original = [1, 2, 3]; byte[] changed = [9, 8, 7];
        static string Hash(byte[] value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(value));
        await File.WriteAllBytesAsync(input, original);
        await Transport.UploadObjectAsync("config/artifact.7z", input, 3, Hash(original), new(3), default);
        await File.WriteAllBytesAsync(input, changed);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Transport.UploadObjectAsync("config/artifact.7z", input, 3, Hash(changed), new(3), default));
        var output = Path.Combine(_root, "download");
        await Transport.DownloadObjectAsync("config/artifact.7z", output, 3, new(3), default);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(output));
        Assert.IsTrue(await Transport.HasVerifiedObjectAsync("config/artifact.7z", 3, Hash(original), default));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task LocalDirectoriesAndReservedPreviewFilesNeverReachTheRemote(bool previewFile, bool relative)
    {
        var directory = Path.Combine(_root, previewFile ? PreviewStoragePaths.DirectoryName : "directory");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "data"); File.WriteAllText(file, "must not upload");
        var source = previewFile ? file : directory;
        if (relative) source = Path.GetRelativePath(Environment.CurrentDirectory, source);
        if (previewFile) await Assert.ThrowsAsync<IOException>(() => Transport.UploadAsync(ReplicaId.New(), source, default));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Transport.UploadAsync(ReplicaId.New(), source, default));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "repository")));
    }

    [TestMethod]
    public async Task CanceledSingleFileUploadDoesNotPublishAnything()
    {
        var file = Path.Combine(_root, "source"); File.WriteAllText(file, "synthetic");
        await Assert.ThrowsAsync<OperationCanceledException>(() => Transport.UploadAsync(ReplicaId.New(), file, new CancellationToken(true)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "repository")));
    }
}
