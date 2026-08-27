using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRuntimeTests
{
    private string _root = null!;
    private HistoryConfigId _configId;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindHistoryRuntimeTests", Guid.NewGuid().ToString("N"));
        _configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Runtime_QueryBoundaryReadsIndexedFactsAndReportsMissingLocalState()
    {
        var repository = await CreateRepositoryAsync(_configId, "query");
        var codec = new HistoryPackCodec();
        var sourceId = SourceId.New();
        var version = CreateVersion(sourceId);
        var representation = new VersionRepresentation(
            RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "7z", [],
            RestoreStrategy.Exact, null, null, null);
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), _configId, DateTimeOffset.UtcNow.AddSeconds(1), null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, version.SourceDescriptorSnapshot, version.VersionId,
                CheckpointSourceDisposition.Captured)]);
        var run = new BackupRun(
            RunId.New(), _configId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(2),
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(sourceId, BackupRunSourceOutcome.Captured, version.VersionId, [])],
            checkpoint.CheckpointId, []);
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddSeconds(2), BranchUpdateReason.Created);
        var pack = new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { version, representation, checkpoint, run, branch }.Select(value => codec.CreateObject(value)));
        await repository.CommitAsync(pack);
        await using var runtime = new HistoryRuntime(repository, codec);

        await runtime.InitializeAsync();

        Assert.IsTrue(runtime.Health.HasFlag(HistoryRuntimeHealth.WorkspaceRecoveryRequired));
        Assert.IsTrue(runtime.Health.HasFlag(HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired));
        Assert.HasCount(1, await runtime.Query.GetVersionsForSourceAsync(sourceId));
        Assert.AreEqual(checkpoint.CheckpointId, (await runtime.Query.GetCheckpointAsync(checkpoint.CheckpointId))!.CheckpointId);
        Assert.HasCount(1, await runtime.Query.GetBranchTipsAsync(branch.BranchId));
        Assert.HasCount(1, await runtime.Query.GetRunsAsync());
        Assert.HasCount(1, await runtime.Query.GetRepresentationsAsync(version.VersionId));
        Assert.HasCount(3, await runtime.Query.GetTimelineAsync());
    }

    [TestMethod]
    public async Task RuntimeManager_CanonicalConfigKeyCreatesOneRuntime()
    {
        var guid = Guid.NewGuid();
        var firstId = new HistoryConfigId(guid.ToString("D"));
        var secondId = new HistoryConfigId(guid.ToString("N"));
        var factoryCalls = 0;
        await using var manager = new HistoryRuntimeManager();

        Task<FileHistoryRepository> Factory(HistoryConfigId id, CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return CreateRepositoryAsync(id, "manager");
        }

        var runtimes = await Task.WhenAll(
            manager.GetOrCreateAsync(firstId, Factory),
            manager.GetOrCreateAsync(secondId, Factory));

        Assert.AreSame(runtimes[0], runtimes[1]);
        Assert.AreEqual(1, factoryCalls);
        Assert.IsTrue(manager.TryGet(firstId, out var current));
        Assert.AreSame(runtimes[0], current);
    }

    [TestMethod]
    public async Task MutationGate_SerializesOnlyCriticalSections()
    {
        using var gate = new HistoryMutationGate(_configId);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = Task.Run(async () =>
        {
            await using var lease = await gate.EnterAsync();
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;
        var second = Task.Run(async () =>
        {
            await using var lease = await gate.EnterAsync();
            secondEntered = true;
        });

        await Task.Delay(50);
        Assert.IsFalse(secondEntered);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.IsTrue(secondEntered);
    }

    [TestMethod]
    public void ChangeFeed_IsOrderedAndObserverFailureCannotBreakPublication()
    {
        var feed = new HistoryChangeFeed();
        var delivered = new List<HistoryChange>();
        using var throwing = feed.Subscribe(_ => throw new InvalidOperationException("observer failed"));
        using var recording = feed.Subscribe(delivered.Add);

        var first = feed.Publish(_configId, HistoryChangeKind.PacksImported, ["a"]);
        var second = feed.Publish(_configId, HistoryChangeKind.IndexRebuilt);

        Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual(2L, second.Sequence);
        Assert.HasCount(2, delivered);
        Assert.AreEqual(2L, feed.CurrentSequence);
    }

    private async Task<FileHistoryRepository> CreateRepositoryAsync(
        HistoryConfigId configId,
        string name)
    {
        var paths = new HistoryRepositoryPaths(Path.Combine(_root, name));
        var repository = new FileHistoryRepository(configId, paths);
        await repository.InitializeAsync();
        return repository;
    }

    private SourceVersion CreateVersion(SourceId sourceId)
        => new(
            VersionId.New(), _configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("source", "C:\\source"), null,
            HistoryProvenance.Native("test"));
}
