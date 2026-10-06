using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
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
    public async Task EmptyRepositoryMissingStateIsHealthyButCorruptionAndTransactionsAreNot()
    {
        await using var runtime = new HistoryRuntime(await CreateRepositoryAsync(_configId, "empty"));
        await runtime.InitializeAsync();
        Assert.AreEqual(HistoryRuntimeHealth.Ready, runtime.Health);
        Assert.IsNull(runtime.HealthDiagnostic);
        var local = runtime.Repository.Paths.LocalStateRoot;
        Directory.CreateDirectory(local);
        var workspace = Path.Combine(local, "workspace.json");
        File.WriteAllText(workspace, "invalid json");
        await runtime.RefreshLocalStateHealthAsync();
        Assert.IsTrue(runtime.Health.HasFlag(HistoryRuntimeHealth.WorkspaceRecoveryRequired));
        StringAssert.Contains(runtime.HealthDiagnostic!, "workspace.json: Corrupt");
        StringAssert.Contains(runtime.HealthDiagnostic!, _configId.ToString());
        using (var held = new FileStream(workspace, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await runtime.RefreshLocalStateHealthAsync();
            StringAssert.Contains(runtime.HealthDiagnostic!, "workspace.json: Inaccessible");
        }
        File.Delete(workspace);
        File.WriteAllText(Path.Combine(local, "replicas.json"), "invalid json");
        await runtime.RefreshLocalStateHealthAsync();
        Assert.IsTrue(runtime.Health.HasFlag(HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired));
        StringAssert.Contains(runtime.HealthDiagnostic!, "replicas.json: Corrupt");
        File.Delete(Path.Combine(local, "replicas.json"));
        Directory.CreateDirectory(runtime.Repository.Paths.TransactionsRoot);
        var journal = Path.Combine(runtime.Repository.Paths.TransactionsRoot, "restore-journal.json");
        File.WriteAllText(journal, "null");
        await runtime.RefreshLocalStateHealthAsync();
        Assert.AreNotEqual(HistoryRuntimeHealth.Ready, runtime.Health);
        StringAssert.Contains(runtime.HealthDiagnostic!, "transactions");
        File.Delete(journal);
        await runtime.RefreshLocalStateHealthAsync();
        Assert.AreEqual(HistoryRuntimeHealth.Ready, runtime.Health);
        Assert.IsNull(runtime.HealthDiagnostic);
        await runtime.WorkspaceStore.SaveAsync(new(_configId, 0, []), -1);
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(_configId, 0, []), -1);
        await runtime.RefreshLocalStateHealthAsync();
        Assert.AreEqual(HistoryRuntimeHealth.Ready, runtime.Health);
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
            MaterializationFidelity.Exact, null, null, null);
        var checkpoint = new SourceCheckpoint(
            CheckpointId.New(), _configId, DateTimeOffset.UtcNow.AddSeconds(1), null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, version.SourceDescriptorSnapshot, version.VersionId,
                CheckpointSourceDisposition.Captured)]);
        var run = new BackupRun(
            RunId.New(), _configId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(2),
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(sourceId, BackupRunSourceOutcome.Captured, version.VersionId, [])], []);
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddSeconds(2), BranchUpdateReason.Created, sourceId: checkpoint.SourceId);
        var pack = new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { version, representation, checkpoint, run, branch }.Select(value => codec.CreateObject(value)));
        await repository.CommitAsync(pack);
        await using var runtime = new HistoryRuntime(repository, codec);

        await runtime.InitializeAsync();

        Assert.IsTrue(runtime.Health.HasFlag(HistoryRuntimeHealth.WorkspaceRecoveryRequired));
        Assert.IsTrue(runtime.Health.HasFlag(HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired));
        StringAssert.Contains(runtime.HealthDiagnostic!, "workspace.json: Missing");
        StringAssert.Contains(runtime.HealthDiagnostic!, "replicas.json: Missing");
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
    public async Task RuntimeManager_RemoveAllowsFreshRuntimeForSameConfig()
    {
        var factoryCalls = 0;
        await using var manager = new HistoryRuntimeManager();
        Task<FileHistoryRepository> Factory(HistoryConfigId id, CancellationToken _)
        {
            var call = Interlocked.Increment(ref factoryCalls);
            return CreateRepositoryAsync(id, $"manager-remove-{call}");
        }

        var first = await manager.GetOrCreateAsync(_configId, Factory);
        var removed = await manager.RemoveAsync(_configId);
        Assert.AreSame(first, removed);
        await removed!.DisposeAsync();

        var second = await manager.GetOrCreateAsync(_configId, Factory);
        Assert.AreNotSame(first, second);
        Assert.AreEqual(2, factoryCalls);
    }

    [TestMethod]
    public async Task RuntimeManager_FailedCreationCanRetry()
    {
        var factoryCalls = 0;
        await using var manager = new HistoryRuntimeManager();
        async Task<FileHistoryRepository> Factory(HistoryConfigId id, CancellationToken _)
        {
            if (Interlocked.Increment(ref factoryCalls) == 1)
                throw new IOException("simulated first initialization failure");
            return await CreateRepositoryAsync(id, "manager-retry");
        }

        await Assert.ThrowsExactlyAsync<IOException>(() => manager.GetOrCreateAsync(_configId, Factory));
        var runtime = await manager.GetOrCreateAsync(_configId, Factory);

        Assert.IsNotNull(runtime);
        Assert.AreEqual(2, factoryCalls);
    }

    [TestMethod]
    public async Task RuntimeManager_TryGetDoesNotWaitForPendingInitialization()
    {
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var manager = new HistoryRuntimeManager();

        async Task<FileHistoryRepository> Factory(HistoryConfigId id, CancellationToken _)
        {
            factoryEntered.SetResult();
            await releaseFactory.Task;
            return await CreateRepositoryAsync(id, "manager-pending");
        }

        var initialization = manager.GetOrCreateAsync(_configId, Factory);
        await factoryEntered.Task;

        try
        {
            Assert.IsFalse(manager.TryGet(_configId, out var pending));
            Assert.IsNull(pending);
        }
        finally
        {
            releaseFactory.TrySetResult();
        }

        var runtime = await initialization;
        Assert.IsTrue(manager.TryGet(_configId, out var ready));
        Assert.AreSame(runtime, ready);
    }

    [TestMethod]
    public async Task RuntimeManager_CancelledWaiterDoesNotCancelSharedInitialization()
    {
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var factoryTokenCanBeCanceled = true;
        await using var manager = new HistoryRuntimeManager();
        using var cancellation = new CancellationTokenSource();

        async Task<FileHistoryRepository> Factory(HistoryConfigId id, CancellationToken token)
        {
            Interlocked.Increment(ref factoryCalls);
            factoryTokenCanBeCanceled = token.CanBeCanceled;
            factoryEntered.SetResult();
            await releaseFactory.Task;
            return await CreateRepositoryAsync(id, "manager-cancelled-waiter");
        }

        var cancelledWaiter = manager.GetOrCreateAsync(_configId, Factory, cancellation.Token);
        await factoryEntered.Task;
        var survivingWaiter = manager.GetOrCreateAsync(_configId, Factory);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelledWaiter);
        }
        finally
        {
            releaseFactory.TrySetResult();
        }

        var runtime = await survivingWaiter;
        Assert.IsNotNull(runtime);
        Assert.AreEqual(1, factoryCalls);
        Assert.IsFalse(factoryTokenCanBeCanceled);
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
