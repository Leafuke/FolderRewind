using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMetadataSyncTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindMetadataSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task TwoDevicesConvergeByPackSetUnionWithoutChangingWorkspace()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var first = await Runtime("first", configId);
        await using var second = await Runtime("second", configId);
        await CommitVersion(first, "first");
        await CommitVersion(second, "second");
        var workspace = new HistoryWorkspace(configId, 0, null, null, []);
        await second.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);
        var transport = new MemoryTransport();

        Assert.IsTrue((await new HistoryMetadataSyncService(first, transport).SyncAsync()).Succeeded);
        Assert.IsTrue((await new HistoryMetadataSyncService(second, transport).SyncAsync()).Succeeded);
        Assert.IsTrue((await new HistoryMetadataSyncService(first, transport).SyncAsync()).Succeeded);

        Assert.HasCount(2, await first.Repository.ReadAllPacksAsync());
        Assert.HasCount(2, await second.Repository.ReadAllPacksAsync());
        Assert.IsTrue(HistoryRestoreTransactionJournalStore.WorkspaceEquals(
            workspace,
            (await second.WorkspaceStore.LoadAsync()).Value!));
    }

    private async Task<HistoryRuntime> Runtime(string name, HistoryConfigId configId)
    {
        var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, name))));
        await runtime.InitializeAsync();
        return runtime;
    }

    private static Task CommitVersion(HistoryRuntime runtime, string name)
    {
        var version = new SourceVersion(
            VersionId.New(), runtime.ConfigId, SourceId.New(), [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot(name, name), name, HistoryProvenance.Native("test"));
        var codec = new HistoryPackCodec();
        return runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version)]));
    }

    private sealed class MemoryTransport : IHistoryMetadataTransport
    {
        private byte[]? _descriptor;
        private readonly Dictionary<PackId, byte[]> _packs = [];

        public Task<byte[]?> ReadDescriptorAsync(HistoryConfigId configId, CancellationToken cancellationToken)
            => Task.FromResult(_descriptor?.ToArray());

        public Task CreateDescriptorOnceAsync(
            HistoryConfigId configId, byte[] canonicalBytes, CancellationToken cancellationToken)
        {
            if (_descriptor is not null && !_descriptor.AsSpan().SequenceEqual(canonicalBytes))
                throw new IOException("descriptor conflict");
            _descriptor ??= canonicalBytes.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PackId>> ListPacksAsync(
            HistoryConfigId configId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PackId>>(_packs.Keys.ToArray());

        public Task<byte[]> DownloadPackAsync(
            HistoryConfigId configId, PackId packId, CancellationToken cancellationToken)
            => Task.FromResult(_packs[packId].ToArray());

        public Task UploadPackOnceAsync(
            HistoryConfigId configId, PackId packId, byte[] bytes, CancellationToken cancellationToken)
        {
            if (_packs.TryGetValue(packId, out var existing) && !existing.AsSpan().SequenceEqual(bytes))
                throw new IOException("pack conflict");
            _packs[packId] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> LegacyHistoryExistsAsync(
            HistoryConfigId configId, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
