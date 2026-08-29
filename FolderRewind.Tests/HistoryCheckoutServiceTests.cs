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

    [TestMethod]
    public async Task CleanRestoreRemovesUnrelatedTargetFiles()
    {
        var restored = await RestoreSingleVersionAsync(
            CaptureScope.FullSource,
            RestoreStrategy.Exact,
            HistoryRestoreApplyMode.Clean);

        Assert.IsTrue(restored.Result.Succeeded, restored.Result.Diagnostic);
        Assert.IsFalse(File.Exists(Path.Combine(restored.Target, "original.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(restored.Target, "restored.txt")));
        Assert.AreEqual(WorkspaceBaselineRelation.Exact, restored.Relation);
    }

    [TestMethod]
    public async Task OverwriteRestorePreservesUnrelatedTargetFiles()
    {
        var restored = await RestoreSingleVersionAsync(
            CaptureScope.FullSource,
            RestoreStrategy.Exact,
            HistoryRestoreApplyMode.Overwrite);

        Assert.IsTrue(restored.Result.Succeeded, restored.Result.Diagnostic);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(restored.Target, "original.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(restored.Target, "restored.txt")));
        Assert.AreEqual(WorkspaceBaselineRelation.Derived, restored.Relation);
    }

    [TestMethod]
    public async Task PartialSourceForcesOverwriteWhenCleanWasRequested()
    {
        var restored = await RestoreSingleVersionAsync(
            CaptureScope.PartialSource,
            RestoreStrategy.Overlay,
            HistoryRestoreApplyMode.Clean);

        Assert.IsTrue(restored.Result.Succeeded, restored.Result.Diagnostic);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(restored.Target, "original.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(restored.Target, "restored.txt")));
        Assert.AreEqual(WorkspaceBaselineRelation.Derived, restored.Relation);
    }

    private async Task<(HistoryRestoreResult Result, string Target, WorkspaceBaselineRelation Relation)> RestoreSingleVersionAsync(
        CaptureScope captureScope,
        RestoreStrategy restoreStrategy,
        HistoryRestoreApplyMode requestedMode)
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var repository = new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "repository-" + Guid.NewGuid().ToString("N"))));
        await using var history = new HistoryRuntime(repository);
        await history.InitializeAsync();
        var sourceId = SourceId.New();
        var version = new SourceVersion(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            captureScope, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("source", "source"), null, HistoryProvenance.Native("test"));
        var representation = new VersionRepresentation(
            RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "test", [],
            restoreStrategy, null, null, null);
        var codec = new HistoryPackCodec();
        await repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version), codec.CreateObject(representation)]));
        await history.EnsureIndexCurrentAsync();
        var workspace = new HistoryWorkspace(configId, 0, null, null, []);
        await history.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);
        var target = CreateTarget("restore-" + Guid.NewGuid().ToString("N"), "old");
        var restore = new HistoryRestoreService(
            history,
            new RepresentationRuntime([new ExactTestRepresentationHandler()]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend());

        var result = await restore.RestoreVersionAsync(
            version.VersionId,
            new HistoryRestoreSourceBinding(sourceId, target),
            workspace,
            requestedMode);
        var updatedWorkspace = (await history.WorkspaceStore.LoadAsync()).Value!;
        return (result, target, updatedWorkspace.SourceBaselines.Single().Relation);
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
                context.Representation.RestoreStrategy == RestoreStrategy.Overlay
                    ? MaterializationFidelity.Overlay
                    : MaterializationFidelity.Exact,
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
            HistoryRestoreApplyMode applyMode,
            HistoryRestoreRollbackSnapshot rollbackSnapshot,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _applyCount) == 2)
                throw new IOException("Injected second-source apply failure.");
            return inner.ApplyAsync(source, stagingDirectory, applyMode, rollbackSnapshot, cancellationToken);
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
