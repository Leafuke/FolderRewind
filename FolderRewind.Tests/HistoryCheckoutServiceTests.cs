using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryCheckoutServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindCheckoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task MultiSourceCheckoutFailureRestoresEverySourceAndLeavesWorkspaceUnchanged()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var repository = new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "repository")));
        await using var history = new HistoryRuntime(repository);
        await history.InitializeAsync();

        var sourceOne = SourceId.New();
        var sourceTwo = SourceId.New();
        var versionOne = Version(configId, sourceOne, "one");
        var versionTwo = Version(configId, sourceTwo, "two");
        var representationOne = Representation(versionOne.VersionId);
        var representationTwo = Representation(versionTwo.VersionId);
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(),
            configId,
            DateTimeOffset.UtcNow,
            createdByRunId: null,
            HistoryProvenance.Native("test"),
            [
                new CheckpointSource(
                    sourceOne, versionOne.SourceDescriptorSnapshot, versionOne.VersionId,
                    CheckpointSourceDisposition.Captured),
                new CheckpointSource(
                    sourceTwo, versionTwo.SourceDescriptorSnapshot, versionTwo.VersionId,
                    CheckpointSourceDisposition.Captured)
            ]);
        var branch = new BranchUpdate(
            BranchUpdateId.New(),
            BranchId.New(),
            [],
            "main",
            checkpoint.CheckpointId,
            isDeleted: false,
            DateTimeOffset.UtcNow,
            BranchUpdateReason.Created);
        var codec = new HistoryPackCodec();
        await repository.CommitAsync(new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            new object[] { versionOne, versionTwo, representationOne, representationTwo, checkpoint, branch }
                .Select(fact => codec.CreateObject(fact))));

        var expectedWorkspace = new HistoryWorkspace(configId, 0, null, null, []);
        await history.WorkspaceStore.SaveAsync(expectedWorkspace, HistoryWorkspaceStore.MissingRevision);
        var targetOne = CreateTarget("source-one", "old-one");
        var targetTwo = CreateTarget("source-two", "old-two");
        var representationRuntime = new RepresentationRuntime([new ExactTestRepresentationHandler()]);
        var mutation = new FailSecondApplyBackend(new FileSystemHistoryRestoreMutationBackend());
        var restore = new HistoryRestoreService(
            history,
            representationRuntime,
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            mutation);
        var checkout = new HistoryCheckoutService(history, restore);

        var result = await checkout.CheckoutAsync(
            branch.UpdateId,
            [
                new HistoryRestoreSourceBinding(sourceOne, targetOne),
                new HistoryRestoreSourceBinding(sourceTwo, targetTwo)
            ],
            expectedWorkspace,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges);

        Assert.AreEqual(HistoryRestoreStatus.Failed, result.Status);
        Assert.IsFalse(result.WorkspaceUpdated);
        Assert.AreEqual("old-one", File.ReadAllText(Path.Combine(targetOne, "original.txt")));
        Assert.AreEqual("old-two", File.ReadAllText(Path.Combine(targetTwo, "original.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(targetOne, "restored.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(targetTwo, "restored.txt")));
        var actualWorkspace = (await history.WorkspaceStore.LoadAsync()).Value!;
        Assert.IsTrue(HistoryRestoreTransactionJournalStore.WorkspaceEquals(expectedWorkspace, actualWorkspace));
    }

    private string CreateTarget(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "original.txt"), content);
        return path;
    }

    private static SourceVersion Version(HistoryConfigId configId, SourceId sourceId, string name)
        => new(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot(name, name), name, HistoryProvenance.Native("test"));

    private static VersionRepresentation Representation(VersionId versionId)
        => new(
            RepresentationId.New(), versionId, RepresentationKind.CoreFull, "test", [],
            RestoreStrategy.Exact, null, null, null);

    private sealed class ExactTestRepresentationHandler : IRepresentationHandler
    {
        public bool CanHandle(VersionRepresentation representation) => representation.Format == "test";

        public ValueTask<RepresentationAssessment> AssessAsync(
            RepresentationAssessmentContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RepresentationAssessment(
                context.Representation.RepresentationId,
                HistoryReadiness.Ready,
                MaterializationFidelity.Exact,
                [],
                []));

        public ValueTask MaterializeAsync(
            RepresentationMaterializationContext context,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(context.StagingDirectory);
            File.WriteAllText(Path.Combine(context.StagingDirectory, "restored.txt"), "new");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailSecondApplyBackend(IHistoryRestoreMutationBackend inner)
        : IHistoryRestoreMutationBackend
    {
        private int _applyCount;

        public HistoryRestoreRollbackSnapshot PlanRollback(
            HistoryRestoreSourceBinding source,
            HistoryTransactionId transactionId)
            => inner.PlanRollback(source, transactionId);

        public Task PrepareRollbackAsync(
            HistoryRestoreRollbackSnapshot snapshot,
            CancellationToken cancellationToken)
            => inner.PrepareRollbackAsync(snapshot, cancellationToken);

        public Task ApplyAsync(
            HistoryRestoreSourceBinding source,
            string stagingDirectory,
            MaterializationFidelity fidelity,
            HistoryRestoreRollbackSnapshot rollbackSnapshot,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _applyCount) == 2)
                throw new IOException("Injected second-source apply failure.");
            return inner.ApplyAsync(source, stagingDirectory, fidelity, rollbackSnapshot, cancellationToken);
        }

        public Task RollbackAsync(
            HistoryRestoreRollbackSnapshot snapshot,
            CancellationToken cancellationToken)
            => inner.RollbackAsync(snapshot, cancellationToken);

        public Task CommitAsync(
            HistoryRestoreRollbackSnapshot snapshot,
            CancellationToken cancellationToken)
            => inner.CommitAsync(snapshot, cancellationToken);
    }
}
