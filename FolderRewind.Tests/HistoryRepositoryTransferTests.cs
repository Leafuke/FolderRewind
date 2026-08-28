using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRepositoryTransferTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindTransferTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task MetadataTransferImportsAsIdempotentPackUnionForManifestConfigIdentity()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var source = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "source"))));
        await source.InitializeAsync();
        var version = new SourceVersion(
            VersionId.New(), configId, SourceId.New(), [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot("source", "source"), null, HistoryProvenance.Native("test"));
        var codec = new HistoryPackCodec();
        await source.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version)]));
        var transferPath = Path.Combine(_root, "history.frhistory");
        var configDirectory = Path.Combine(_root, "target-config");
        var transfer = new HistoryRepositoryTransferService();

        await transfer.ExportAsync(source, transferPath);
        var first = await transfer.ImportAsync(transferPath, configDirectory);
        var second = await transfer.ImportAsync(transferPath, configDirectory);

        Assert.AreEqual(configId, first.ConfigId);
        Assert.AreEqual(1, first.InstalledPacks);
        Assert.IsTrue(first.ImportedAsOrphanRepository);
        Assert.AreEqual(0, second.InstalledPacks);
        Assert.AreEqual(1, second.DuplicatePacks);
    }
}
