using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryIndexAndLocalStateTests
{
    private string _root = null!;
    private HistoryConfigId _configId;
    private HistoryPackCodec _codec = null!;
    private FileHistoryRepository _repository = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindHistoryIndexTests", Guid.NewGuid().ToString("N"));
        _configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        _codec = new HistoryPackCodec();
        _repository = new FileHistoryRepository(_configId, new HistoryRepositoryPaths(_root), _codec);
        await _repository.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _repository.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Index_CanBeDeletedAndRebuiltWithDivergentBranchTips()
    {
        var source = SourceId.New();
        var version = CreateVersion(source);
        var checkpoint = CreateCheckpoint(source, version.VersionId);
        var branchId = BranchId.New();
        var rootUpdate = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var firstTip = new BranchUpdate(
            BranchUpdateId.New(), branchId, [rootUpdate.UpdateId], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddSeconds(1), BranchUpdateReason.Backup);
        var secondTip = new BranchUpdate(
            BranchUpdateId.New(), branchId, [rootUpdate.UpdateId], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddSeconds(2), BranchUpdateReason.Backup);
        var pack = CreatePack(version, checkpoint, rootUpdate, firstTip, secondTip);
        await _repository.CommitAsync(pack);
        var indexPath = Path.Combine(_repository.Paths.IndexRoot, "history-index.db");
        using var index = new HistoryIndex(indexPath, _codec);

        await index.RebuildAsync(await _repository.ReadAllPacksAsync());
        var firstCount = await index.GetObjectCountAsync();
        var firstTips = await index.GetBranchTipsAsync(branchId);
        File.Delete(indexPath);
        await index.RebuildAsync(await _repository.ReadAllPacksAsync());

        Assert.AreEqual(5, firstCount);
        Assert.HasCount(2, firstTips);
        Assert.AreEqual(firstCount, await index.GetObjectCountAsync());
        CollectionAssert.AreEquivalent(
            new[] { firstTip.UpdateId, secondTip.UpdateId },
            (await index.GetBranchTipsAsync(branchId)).ToArray());
    }

    [TestMethod]
    public async Task Index_RebuildCollapsesIdempotentDefinitionsFromDifferentPacks()
    {
        var version = CreateVersion(SourceId.New());
        await _repository.CommitAsync(CreatePack(version));
        await _repository.CommitAsync(CreatePack(version));
        using var index = new HistoryIndex(
            Path.Combine(_repository.Paths.IndexRoot, "history-index.db"),
            _codec);

        await index.RebuildAsync(await _repository.ReadAllPacksAsync());

        Assert.AreEqual(1, await index.GetObjectCountAsync());
    }

    [TestMethod]
    public async Task CrossBranchParentDoesNotConsumeSourceBranchLocalTip()
    {
        var source = SourceId.New();
        var version = CreateVersion(source);
        var checkpoint = CreateCheckpoint(source, version.VersionId);
        var experimentId = BranchId.New();
        var mainId = BranchId.New();
        var experimentTip = new BranchUpdate(
            BranchUpdateId.New(), experimentId, [], "experiment", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var mainTip = new BranchUpdate(
            BranchUpdateId.New(), mainId, [experimentTip.UpdateId], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddSeconds(1), BranchUpdateReason.Backup);
        await _repository.CommitAsync(CreatePack(version, checkpoint, experimentTip, mainTip));
        using var index = new HistoryIndex(
            Path.Combine(_repository.Paths.IndexRoot, "history-index.db"),
            _codec);

        await index.RebuildAsync(await _repository.ReadAllPacksAsync());

        CollectionAssert.AreEqual(
            new[] { experimentTip.UpdateId },
            (await index.GetBranchTipsAsync(experimentId)).ToArray());
        CollectionAssert.AreEqual(
            new[] { mainTip.UpdateId },
            (await index.GetBranchTipsAsync(mainId)).ToArray());
        var updates = new[] { experimentTip, mainTip };
        CollectionAssert.AreEqual(
            new[] { mainTip.UpdateId },
            HistoryBranchProjection.LocalLineage(mainTip, updates).Select(item => item.UpdateId).ToArray());
        Assert.IsTrue(HistoryBranchProjection.IsGlobalAncestor(
            experimentTip.UpdateId,
            mainTip.UpdateId,
            updates));
    }

    [TestMethod]
    public async Task Workspace_MissingIsUnknownAndRevisionConflictCannotOverwrite()
    {
        var path = Path.Combine(_repository.Paths.LocalStateRoot, "workspace.json");
        using var store = new HistoryWorkspaceStore(_configId, path);
        var missing = await store.LoadAsync();
        var branchId = BranchId.New();
        var updateId = BranchUpdateId.New();
        var workspace = new HistoryWorkspace(
            _configId, 0, branchId, updateId,
            [new WorkspaceSourceBaseline(SourceId.New(), null, WorkspaceBaselineRelation.Unknown)]);

        await store.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);
        var conflicting = new HistoryWorkspace(_configId, 1, branchId, updateId, workspace.SourceBaselines);

        Assert.AreEqual(DeviceLocalStateStatus.Missing, missing.Status);
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(
            () => store.SaveAsync(conflicting, HistoryWorkspaceStore.MissingRevision));
        Assert.AreEqual(0, (await store.LoadAsync()).Value!.StateRevision);
    }

    [TestMethod]
    public async Task LocalReplicaCatalog_IsDurableOutsideDisposableIndex()
    {
        var catalogPath = Path.Combine(_repository.Paths.LocalStateRoot, "replicas.json");
        var indexPath = Path.Combine(_repository.Paths.IndexRoot, "history-index.db");
        using var catalogStore = new LocalReplicaCatalogStore(_configId, catalogPath);
        var oldDestination = Path.Combine(_root, "destination-old", "archive.7z");
        var newDestination = Path.Combine(_root, "destination-new", "archive.7z");
        var entries = new[]
        {
            new LocalReplicaCatalogEntry(
                RepresentationId.New(), LocalReplicaId.New(),
                LocalReplicaLocator.ControlledAbsolute(oldDestination), DateTimeOffset.UtcNow),
            new LocalReplicaCatalogEntry(
                RepresentationId.New(), LocalReplicaId.New(),
                LocalReplicaLocator.ControlledAbsolute(newDestination), DateTimeOffset.UtcNow)
        };
        await catalogStore.SaveAsync(
            new LocalReplicaCatalog(_configId, 0, entries),
            LocalReplicaCatalogStore.MissingRevision);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, "disposable");
        File.Delete(indexPath);

        var loaded = (await catalogStore.LoadAsync()).Value!;
        Assert.HasCount(2, loaded.Entries);
        Assert.AreEqual(Path.GetFullPath(oldDestination), loaded.Entries[0].Locator.Resolve());
        Assert.AreEqual(Path.GetFullPath(newDestination), loaded.Entries[1].Locator.Resolve());
        Assert.IsTrue(File.Exists(catalogPath));
    }

    [TestMethod]
    public async Task JournalRecovery_CompletesWorkspaceAndCatalogAfterPackCommit()
    {
        var workspacePath = Path.Combine(_repository.Paths.LocalStateRoot, "workspace.json");
        var catalogPath = Path.Combine(_repository.Paths.LocalStateRoot, "replicas.json");
        using var workspaceStore = new HistoryWorkspaceStore(_configId, workspacePath);
        using var catalogStore = new LocalReplicaCatalogStore(_configId, catalogPath);
        var recovery = new HistoryLocalStateJournalRecovery(workspaceStore, catalogStore);
        var branchId = BranchId.New();
        var branchUpdateId = BranchUpdateId.New();
        var workspace = new HistoryWorkspace(_configId, 0, branchId, branchUpdateId, []);
        var catalog = new LocalReplicaCatalog(_configId, 0, []);
        var pack = CreatePack(CreateVersion(SourceId.New()));
        var journal = HistoryTransactionJournal.Prepared(
            pack.TransactionId,
            pack.PackId,
            [
                HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(
                    workspace, HistoryWorkspaceStore.MissingRevision),
                HistoryLocalStateJournalRecovery.CreateCatalogIntent(
                    catalog, LocalReplicaCatalogStore.MissingRevision)
            ]);
        await _repository.CommitAsync(pack, journal);

        await _repository.Journals.RecoverAsync(
            recovery.ApplyCommittedStateAsync,
            (_, _) => Task.CompletedTask);

        Assert.AreEqual(branchUpdateId, (await workspaceStore.LoadAsync()).Value!.ActiveBranchUpdateId);
        Assert.AreEqual(0, (await catalogStore.LoadAsync()).Value!.CatalogRevision);

        // 恢复可重复执行，不会因 expected revision 已前进而覆盖或报冲突。
        await _repository.Journals.RecoverAsync(
            recovery.ApplyCommittedStateAsync,
            (_, _) => Task.CompletedTask);
    }

    private SourceVersion CreateVersion(SourceId sourceId)
        => new(
            VersionId.New(), _configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("source", "C:\\source"), null,
            HistoryProvenance.Native("test"));

    private ConfigurationCheckpoint CreateCheckpoint(SourceId sourceId, VersionId versionId)
        => new(
            CheckpointId.New(), _configId, DateTimeOffset.UtcNow, null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, new SourceDescriptorSnapshot("source", "C:\\source"),
                versionId, CheckpointSourceDisposition.Captured)]);

    private HistoryCommitPack CreatePack(params object[] values)
        => new(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            values.Select(value => _codec.CreateObject(value)));
}
