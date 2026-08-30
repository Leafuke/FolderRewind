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
    public async Task CheckoutRestoresHistoricalSourceAndPreservesCurrentOnlySourceBaselineAndFiles()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var repository = new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "preserve-repository")));
        await using var history = new HistoryRuntime(repository);
        await history.InitializeAsync();
        var historicalId = SourceId.New();
        var currentOnlyId = SourceId.New();
        var historical = Version(configId, historicalId, "historical");
        var currentOnly = Version(configId, currentOnlyId, "current-only");
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(
                historicalId,
                historical.SourceDescriptorSnapshot,
                historical.VersionId,
                CheckpointSourceDisposition.Captured)]);
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var codec = new HistoryPackCodec();
        await repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[]
            {
                historical, currentOnly, Representation(historical.VersionId),
                Representation(currentOnly.VersionId), checkpoint, branch
            }.Select(item => codec.CreateObject(item))));
        var workspace = new HistoryWorkspace(
            configId,
            0,
            null,
            null,
            [new WorkspaceSourceBaseline(currentOnlyId, currentOnly.VersionId, WorkspaceBaselineRelation.Derived)]);
        await history.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);
        var historicalTarget = CreateTarget("historical-target", "old");
        var currentOnlyTarget = CreateTarget("current-only-target", "preserve-me");
        var restore = new HistoryRestoreService(
            history,
            new RepresentationRuntime([new ExactTestRepresentationHandler()]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend());

        var plan = await new HistoryCheckoutPlanner(history, restore).BuildAsync(
            branch.UpdateId,
            [
                new HistoryRestoreSourceBinding(historicalId, historicalTarget),
                new HistoryRestoreSourceBinding(currentOnlyId, currentOnlyTarget)
            ],
            workspace,
            AssessmentDepth.Deep);
        Assert.AreEqual(HistoryCheckoutReadiness.ProtectionRequired, plan.Readiness);

        var result = await new HistoryCheckoutService(history, restore).CheckoutAsync(
            branch.UpdateId,
            [
                new HistoryRestoreSourceBinding(historicalId, historicalTarget),
                new HistoryRestoreSourceBinding(currentOnlyId, currentOnlyTarget)
            ],
            workspace,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges);

        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual("preserve-me", File.ReadAllText(Path.Combine(currentOnlyTarget, "original.txt")));
        var updated = (await history.WorkspaceStore.LoadAsync()).Value!;
        var preserved = updated.SourceBaselines.Single(item => item.SourceId == currentOnlyId);
        Assert.AreEqual(currentOnly.VersionId, preserved.BaseVersionId);
        Assert.AreEqual(WorkspaceBaselineRelation.Derived, preserved.Relation);
    }

    [TestMethod]
    public async Task MissingHistoricalIdentityReturnsActionableMappingWithoutPathAlias()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var repository = new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "mapping-repository")));
        await using var history = new HistoryRuntime(repository);
        await history.InitializeAsync();
        var historicalId = SourceId.New();
        var differentCurrentId = SourceId.New();
        var version = Version(configId, historicalId, "missing-world");
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(historicalId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var codec = new HistoryPackCodec();
        await repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { version, Representation(version.VersionId), checkpoint, branch }
                .Select(item => codec.CreateObject(item))));
        var workspace = new HistoryWorkspace(configId, 0, null, null, []);
        await history.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);
        var suggestedPath = version.SourceDescriptorSnapshot.PathHint;
        var target = CreateTarget("missing-target", "untouched");
        var restore = new HistoryRestoreService(
            history,
            new RepresentationRuntime([new ExactTestRepresentationHandler()]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend());

        var result = await new HistoryCheckoutService(history, restore).CheckoutAsync(
            branch.UpdateId,
            [new HistoryRestoreSourceBinding(differentCurrentId, suggestedPath)],
            workspace,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges);

        Assert.AreEqual(HistoryRestoreStatus.Blocked, result.Status);
        Assert.AreEqual(HistoryCheckoutReadiness.ConfigurationMappingRequired, result.CheckoutPlan!.Readiness);
        var missing = result.CheckoutPlan.MissingHistoricalSources.Single();
        Assert.AreEqual(historicalId, missing.SourceId);
        Assert.AreEqual(suggestedPath, missing.SuggestedPath);
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(target, "original.txt")));
    }

    [TestMethod]
    public async Task SameSourceIdentityWithDifferentBoundaryRequiresExplicitConfigRepair()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var repository = new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "boundary-mapping-repository")));
        await using var history = new HistoryRuntime(repository);
        await history.InitializeAsync();
        var sourceId = SourceId.New();
        var historicalBoundary = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["region/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            [],
            false);
        var version = new SourceVersion(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("world", "world"), null, HistoryProvenance.Native("test"), historicalBoundary);
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, version.SourceDescriptorSnapshot, version.VersionId,
                CheckpointSourceDisposition.Captured, historicalBoundary)]);
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var codec = new HistoryPackCodec();
        await repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { version, Representation(version.VersionId), checkpoint, branch }
                .Select(item => codec.CreateObject(item))));
        var workspace = new HistoryWorkspace(configId, 0, null, null, []);
        await history.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);
        var restore = new HistoryRestoreService(
            history,
            new RepresentationRuntime([new ExactTestRepresentationHandler()]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend());

        var result = await new HistoryCheckoutService(history, restore).CheckoutAsync(
            branch.UpdateId,
            [new HistoryRestoreSourceBinding(sourceId, Path.Combine(_root, "world"), EffectiveSourceBoundarySnapshot.All)],
            workspace,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges);

        Assert.AreEqual(HistoryCheckoutReadiness.ConfigurationBoundaryChangeRequired, result.CheckoutPlan!.Readiness);
        var mismatch = result.CheckoutPlan.BoundaryMismatches.Single();
        Assert.AreEqual(historicalBoundary.Fingerprint, mismatch.HistoricalBoundary.Fingerprint);
        Assert.AreEqual(EffectiveSourceBoundarySnapshot.All.Fingerprint, mismatch.CurrentBoundary.Fingerprint);
    }

    [TestMethod]
    public async Task CleanRestoreRemovesUnrelatedTargetFiles()
    {
        var restored = await RestoreSingleVersionAsync(
            CaptureScope.FullSource,
            MaterializationFidelity.Exact,
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
            MaterializationFidelity.Exact,
            HistoryRestoreApplyMode.Overwrite);

        Assert.IsTrue(restored.Result.Succeeded, restored.Result.Diagnostic);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(restored.Target, "original.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(restored.Target, "restored.txt")));
        Assert.AreEqual(WorkspaceBaselineRelation.Derived, restored.Relation);
    }

    [TestMethod]
    public async Task PartialSourceCanUseCleanWhenClosureIsExact()
    {
        var restored = await RestoreSingleVersionAsync(
            CaptureScope.PartialSource,
            MaterializationFidelity.Exact,
            HistoryRestoreApplyMode.Clean);

        Assert.IsTrue(restored.Result.Succeeded, restored.Result.Diagnostic);
        Assert.IsFalse(File.Exists(Path.Combine(restored.Target, "original.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(restored.Target, "restored.txt")));
        Assert.AreEqual(WorkspaceBaselineRelation.Exact, restored.Relation);
    }

    private async Task<(HistoryRestoreResult Result, string Target, WorkspaceBaselineRelation Relation)> RestoreSingleVersionAsync(
        CaptureScope captureScope,
        MaterializationFidelity fidelity,
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
            fidelity, null, null, null);
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
            MaterializationFidelity.Exact, null, null, null);

    private sealed class ExactTestRepresentationHandler : IRepresentationHandler
    {
        public bool CanHandle(VersionRepresentation representation) => representation.Format == "test";

        public ValueTask<RepresentationAssessment> AssessAsync(
            RepresentationAssessmentContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RepresentationAssessment(
                context.Representation.RepresentationId,
                HistoryReadiness.Ready,
                context.Representation.Fidelity,
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
