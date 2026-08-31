using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryBranchAndAnnotationTests
{
    private string _root = null!;
    private HistoryConfigId _configId;
    private readonly HistoryPackCodec _codec = new();

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindBranchTests", Guid.NewGuid().ToString("N"));
        _configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task HistoricalBranchCreationDoesNotRestoreOrMoveWorkspace()
    {
        await using var runtime = await CreateRuntimeAsync();
        var seeded = await SeedAsync(runtime);
        var before = (await runtime.WorkspaceStore.LoadAsync()).Value!;

        var created = await runtime.Branches.CreateFromCheckpointAsync(
            seeded.Batch.NewCheckpoint!.CheckpointId,
            "historical");

        Assert.IsFalse(created.Activated);
        Assert.AreNotEqual(before.ActiveBranchId, created.BranchUpdate.BranchId);
        Assert.AreEqual(seeded.Batch.NewCheckpoint.CheckpointId, created.BranchUpdate.TargetCheckpointId);
        var after = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(before.StateRevision, after.StateRevision);
        Assert.AreEqual(before.ActiveBranchId, after.ActiveBranchId);
        Assert.AreEqual(before.ActiveBranchUpdateId, after.ActiveBranchUpdateId);
    }

    [TestMethod]
    public async Task HistoricalBranchCreationRejectsIncompleteConfigurationCheckpoint()
    {
        await using var runtime = await CreateRuntimeAsync("incomplete-branch");
        var sourceId = SourceId.New();
        var incomplete = new ConfigurationCheckpoint(
            CheckpointId.New(),
            _configId,
            DateTimeOffset.UtcNow,
            null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId,
                new SourceDescriptorSnapshot("missing", "C:\\missing"),
                null,
                CheckpointSourceDisposition.Unavailable)]);
        await CommitFactsAsync(runtime, incomplete);

        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(() =>
            runtime.Branches.CreateFromCheckpointAsync(incomplete.CheckpointId, "invalid"));
    }

    [TestMethod]
    public async Task CurrentExactStateReusesCheckpointAndActivatesNewBranch()
    {
        await using var runtime = await CreateRuntimeAsync();
        var seeded = await SeedAsync(runtime);
        var before = (await runtime.WorkspaceStore.LoadAsync()).Value!;

        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(() =>
            runtime.Branches.CreateFromCurrentStateAsync(
                seeded.Snapshot,
                before,
                "must-capture",
                HistoryProvenance.Native("test"),
                HistoryWorkingStateStatus.Dirty));

        var created = await runtime.Branches.CreateFromCurrentStateAsync(
            seeded.Snapshot,
            before,
            "current-state",
            HistoryProvenance.Native("test"),
            HistoryWorkingStateStatus.ExactBaseline);

        Assert.IsTrue(created.Activated);
        Assert.IsNull(created.CreatedCheckpoint);
        Assert.AreEqual(seeded.Batch.NewCheckpoint!.CheckpointId, created.BranchUpdate.TargetCheckpointId);
        var after = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(before.StateRevision + 1, after.StateRevision);
        Assert.AreEqual(created.BranchUpdate.BranchId, after.ActiveBranchId);
        var oldTips = await runtime.Query.GetBranchTipsAsync(seeded.Batch.NewBranchUpdate!.BranchId);
        Assert.HasCount(1, oldTips);
        Assert.AreEqual(seeded.Batch.NewBranchUpdate.UpdateId, oldTips[0].UpdateId);
    }

    [TestMethod]
    public async Task CurrentExactVectorWithoutCheckpointCreatesAggregateOnly()
    {
        await using var runtime = await CreateRuntimeAsync();
        var seeded = await SeedAsync(runtime);
        var before = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var detached = new SourceVersion(
            VersionId.New(),
            _configId,
            seeded.SourceId,
            [seeded.Batch.NewVersions[0].VersionId],
            DateTimeOffset.UtcNow,
            createdByRunId: null,
            CaptureScope.FullSource,
            CaptureOutcome.Recovered,
            [],
            seeded.Snapshot.Sources[0].Descriptor,
            "detached-state",
            HistoryProvenance.Native("test"));
        await CommitFactsAsync(runtime, detached);
        var detachedWorkspace = new HistoryWorkspace(
            _configId,
            before.StateRevision + 1,
            before.ActiveBranchId,
            before.ActiveBranchUpdateId,
            [new WorkspaceSourceBaseline(seeded.SourceId, detached.VersionId, WorkspaceBaselineRelation.Exact)]);
        await runtime.WorkspaceStore.SaveAsync(detachedWorkspace, before.StateRevision);

        var created = await runtime.Branches.CreateFromCurrentStateAsync(
            seeded.Snapshot,
            detachedWorkspace,
            "aggregate",
            HistoryProvenance.Native("test"),
            HistoryWorkingStateStatus.ExactBaseline);

        Assert.IsNotNull(created.CreatedCheckpoint);
        Assert.AreEqual(detached.VersionId, created.CreatedCheckpoint.Sources.Single().VersionId);
        Assert.IsNull(created.CreatedCheckpoint.CreatedByRunId);
        Assert.HasCount(
            2,
            (await runtime.Repository.ReadAllPacksAsync())
                .Single(pack => pack.Pack.PackId == created.PackId)
                .Pack.Objects);
    }

    [TestMethod]
    public async Task RenameDeleteEnforceActiveLastAndNameRules()
    {
        await using var runtime = await CreateRuntimeAsync();
        var seeded = await SeedAsync(runtime);
        var inactive = await runtime.Branches.CreateFromCheckpointAsync(
            seeded.Batch.NewCheckpoint!.CheckpointId,
            "feature");

        var renamed = await runtime.Branches.RenameAsync(inactive.BranchUpdate.BranchId, "Feature-Renamed");
        Assert.AreEqual(inactive.BranchUpdate.UpdateId, renamed.BranchUpdate.ParentUpdateIds.Single());
        Assert.AreEqual(BranchUpdateReason.Renamed, renamed.BranchUpdate.Reason);
        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(
            () => runtime.Branches.CreateFromCheckpointAsync(seeded.Batch.NewCheckpoint.CheckpointId, "feature-renamed"));
        var deleted = await runtime.Branches.DeleteAsync(inactive.BranchUpdate.BranchId);
        Assert.IsTrue(deleted.BranchUpdate.IsDeleted);
        var reusedName = await runtime.Branches.CreateFromCheckpointAsync(
            seeded.Batch.NewCheckpoint.CheckpointId,
            "feature-renamed");
        Assert.AreNotEqual(inactive.BranchUpdate.BranchId, reusedName.BranchUpdate.BranchId);
        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(
            () => runtime.Branches.DeleteAsync(seeded.Batch.NewBranchUpdate!.BranchId));
    }

    [TestMethod]
    public async Task MultiTipBranchBlocksRenameDeleteButLocalTipCanContinueBackup()
    {
        await using var runtime = await CreateRuntimeAsync();
        var seeded = await SeedAsync(runtime);
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var concurrent = new BranchUpdate(
            BranchUpdateId.New(),
            seeded.Batch.NewBranchUpdate!.BranchId,
            [],
            "remote-main",
            seeded.Batch.NewCheckpoint!.CheckpointId,
            isDeleted: false,
            DateTimeOffset.UtcNow,
            BranchUpdateReason.Backup);
        await CommitFactsAsync(runtime, concurrent);

        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(
            () => runtime.Branches.RenameAsync(concurrent.BranchId, "blocked"));
        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(
            () => runtime.Branches.DeleteAsync(concurrent.BranchId));

        var continued = await runtime.Commit.CommitAsync(Request(
            seeded.Snapshot,
            workspace,
            CreateCapture(
                seeded.SourceId,
                "continued",
                workspace.StateRevision,
                seeded.Batch.NewVersions[0].VersionId)));
        Assert.AreEqual(seeded.Batch.NewBranchUpdate.UpdateId, continued.NewBranchUpdate!.ParentUpdateIds.Single());
        Assert.HasCount(2, await runtime.Query.GetBranchTipsAsync(concurrent.BranchId));
    }

    [TestMethod]
    public async Task EquivalentTipsReconcileToSameDeterministicFactOnTwoRepositories()
    {
        await using var firstRuntime = await CreateRuntimeAsync("reconcile-first");
        await using var secondRuntime = await CreateRuntimeAsync("reconcile-second");
        var sourceId = SourceId.New();
        var version = new SourceVersion(
            VersionId.New(), _configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("source", "C:\\source"), null,
            HistoryProvenance.Native("test"));
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), _configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(sourceId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);
        var branchId = BranchId.New();
        var earlier = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddSeconds(-1), BranchUpdateReason.Backup);
        var later = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Backup);
        await CommitFactsAsync(firstRuntime, version, checkpoint, earlier, later);
        await CommitFactsAsync(secondRuntime, version, checkpoint, earlier, later);

        var first = await new HistoryBranchReconciliationService(firstRuntime).ReconcileAsync(
            branchId, [later.UpdateId, earlier.UpdateId]);
        var second = await new HistoryBranchReconciliationService(secondRuntime).ReconcileAsync(
            branchId, [earlier.UpdateId, later.UpdateId]);

        var firstObject = _codec.CreateObject(first.BranchUpdate);
        var secondObject = _codec.CreateObject(second.BranchUpdate);
        Assert.AreEqual(firstObject.Id, secondObject.Id);
        Assert.AreEqual(firstObject.PayloadHash, secondObject.PayloadHash);
        CollectionAssert.AreEqual(firstObject.CanonicalPayload, secondObject.CanonicalPayload);
        Assert.AreEqual(later.CreatedAtUtc, first.BranchUpdate.CreatedAtUtc);
        Assert.AreEqual(BranchUpdateReason.Reconciled, first.BranchUpdate.Reason);
        Assert.HasCount(1, await firstRuntime.Query.GetBranchTipsAsync(branchId));
        CollectionAssert.AreEqual(
            new[] { earlier.UpdateId, later.UpdateId }.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToArray(),
            first.BranchUpdate.ParentUpdateIds.ToArray());
    }

    [TestMethod]
    public async Task NonEquivalentReconciliationRequiresWinnerAndRejectsStaleTipSet()
    {
        await using var runtime = await CreateRuntimeAsync("reconcile-non-equivalent");
        var firstCheckpoint = CheckpointId.New();
        var secondCheckpoint = CheckpointId.New();
        var branchId = BranchId.New();
        var first = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", firstCheckpoint, false,
            DateTimeOffset.UtcNow.AddSeconds(-1), BranchUpdateReason.Backup);
        var second = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", secondCheckpoint, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Backup);
        var source = SourceId.New();
        var version = new SourceVersion(
            VersionId.New(), _configId, source, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("source", "source"), null, HistoryProvenance.Native("test"));
        var checkpointOne = new ConfigurationCheckpoint(
            firstCheckpoint, _configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(source, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);
        var checkpointTwo = new ConfigurationCheckpoint(
            secondCheckpoint, _configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(source, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);
        await CommitFactsAsync(runtime, version, checkpointOne, checkpointTwo, first, second);
        var service = new HistoryBranchReconciliationService(runtime);

        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(
            () => service.ReconcileAsync(branchId, [first.UpdateId, second.UpdateId]));
        var third = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", firstCheckpoint, false,
            DateTimeOffset.UtcNow.AddSeconds(1), BranchUpdateReason.Backup);
        await CommitFactsAsync(runtime, third);
        await Assert.ThrowsExactlyAsync<HistoryBranchCommandException>(
            () => service.ReconcileAsync(
                branchId,
                [first.UpdateId, second.UpdateId],
                selectedWinnerTipId: second.UpdateId));
        var reconciled = await service.ReconcileAsync(
            branchId,
            [first.UpdateId, second.UpdateId, third.UpdateId],
            selectedWinnerTipId: second.UpdateId);
        Assert.AreEqual(secondCheckpoint, reconciled.BranchUpdate.TargetCheckpointId);
    }

    [TestMethod]
    public void BranchProjectionReportsCloudNameCollisionWithoutDroppingEitherBranch()
    {
        var first = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "Same", CheckpointId.New(), false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var second = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "same", CheckpointId.New(), false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);

        var projection = HistoryBranchProjection.Query([first, second]);

        Assert.HasCount(2, projection.Branches);
        Assert.HasCount(1, projection.BranchNameCollisions);
        Assert.IsTrue(projection.Branches.All(branch => branch.HasNameCollision));
    }

    [TestMethod]
    public void AnnotationProjectionAppliesConcurrentSafetyRules()
    {
        var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var pinTrue = Annotation(target, HistoryAnnotationKind.Pin, "true", now);
        var pinFalse = Annotation(target, HistoryAnnotationKind.Pin, "false", now.AddMilliseconds(1));
        var hidden = Annotation(target, HistoryAnnotationKind.Suppression, "true", now);
        var visible = Annotation(target, HistoryAnnotationKind.Suppression, "false", now.AddMilliseconds(1));
        var commentOne = Annotation(target, HistoryAnnotationKind.Comment, "one", now);
        var commentTwo = Annotation(target, HistoryAnnotationKind.Comment, "two", now.AddMilliseconds(1));

        var projection = HistoryAnnotationProjection.Project(
            target,
            [pinTrue, pinFalse, hidden, visible, commentOne, commentTwo]);

        Assert.IsTrue(projection.IsPinned);
        Assert.IsFalse(projection.IsSuppressed);
        Assert.AreEqual("two", projection.EffectiveComment);
        Assert.HasCount(2, projection.Comments);
    }

    [TestMethod]
    public async Task AnnotationCommandParentsEveryConcurrentTipToResolveConflict()
    {
        await using var runtime = await CreateRuntimeAsync();
        var seeded = await SeedAsync(runtime);
        var target = new HistoryAnnotationTarget(
            HistoryAnnotationTargetKind.Version,
            seeded.Batch.NewVersions[0].VersionId.Value);
        var first = Annotation(target, HistoryAnnotationKind.Pin, "true", DateTimeOffset.UtcNow);
        var second = Annotation(target, HistoryAnnotationKind.Pin, "false", DateTimeOffset.UtcNow.AddMilliseconds(1));
        await CommitFactsAsync(runtime, first, second);

        var resolved = await runtime.Annotations.SetPinAsync(target, pinned: false);
        var projection = await runtime.Query.GetAnnotationProjectionAsync(target);

        CollectionAssert.AreEquivalent(
            new[] { first.UpdateId, second.UpdateId },
            resolved.ParentUpdateIds.ToArray());
        Assert.IsFalse(projection.IsPinned);
        Assert.HasCount(1, projection.Tips[HistoryAnnotationKind.Pin]);
    }

    [TestMethod]
    public void MaterializationProjectionIsRetainedWinsUntilCausalRelease()
    {
        var versionId = VersionId.New();
        var retained = Policy(versionId, MaterializationPolicyState.Retained, []);
        var released = Policy(versionId, MaterializationPolicyState.Released, []);
        var concurrent = MaterializationPolicyProjection.Project(versionId, [retained, released]);
        var causalRelease = Policy(
            versionId,
            MaterializationPolicyState.Released,
            [retained.UpdateId, released.UpdateId]);

        Assert.AreEqual(MaterializationPolicyState.Retained, concurrent.EffectiveState);
        Assert.AreEqual(
            MaterializationPolicyState.Released,
            MaterializationPolicyProjection.Project(versionId, [retained, released, causalRelease]).EffectiveState);
    }

    [TestMethod]
    public async Task MaterializationCommandBuildsCausalChainAndRejectsProtectedRelease()
    {
        await using var runtime = await CreateRuntimeAsync();
        var standaloneSource = SourceId.New();
        var standalone = new SourceVersion(
            VersionId.New(), _configId, standaloneSource, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot("standalone", "C:\\standalone"),
            "standalone", HistoryProvenance.Native("test"));
        await CommitFactsAsync(runtime, standalone);

        var release = await runtime.MaterializationPolicies.SetAsync(
            standalone.VersionId,
            MaterializationPolicyState.Released,
            "release");
        var retain = await runtime.MaterializationPolicies.SetAsync(
            standalone.VersionId,
            MaterializationPolicyState.Retained,
            "retain");
        Assert.AreEqual(release.UpdateId, retain.ParentUpdateIds.Single());
        Assert.AreEqual(
            MaterializationPolicyState.Retained,
            (await runtime.Query.GetMaterializationPolicyProjectionAsync(standalone.VersionId)).EffectiveState);

        var pinnedCheckpoint = new ConfigurationCheckpoint(
            CheckpointId.New(),
            _configId,
            DateTimeOffset.UtcNow,
            null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(
                standaloneSource,
                standalone.SourceDescriptorSnapshot,
                standalone.VersionId,
                CheckpointSourceDisposition.CarriedForward)]);
        await CommitFactsAsync(runtime, pinnedCheckpoint);
        await runtime.Annotations.SetPinAsync(
            new HistoryAnnotationTarget(
                HistoryAnnotationTargetKind.Checkpoint,
                pinnedCheckpoint.CheckpointId.Value),
            pinned: true);
        await Assert.ThrowsExactlyAsync<MaterializationPolicyCommandException>(
            () => runtime.MaterializationPolicies.SetAsync(
                standalone.VersionId,
                MaterializationPolicyState.Released,
                "checkpoint pin blocks release"));

        var seeded = await SeedAsync(runtime, "protected");
        await Assert.ThrowsExactlyAsync<MaterializationPolicyCommandException>(
            () => runtime.MaterializationPolicies.SetAsync(
                seeded.Batch.NewVersions[0].VersionId,
                MaterializationPolicyState.Released,
                "must fail"));
    }

    private async Task<(HistoryConfigSnapshot Snapshot, SourceId SourceId, HistoryCommitBatch Batch)> SeedAsync(
        HistoryRuntime runtime,
        string content = "seed")
    {
        var sourceId = SourceId.New();
        var snapshot = new HistoryConfigSnapshot(
            _configId,
            [new HistoryConfigSourceSnapshot(sourceId, new SourceDescriptorSnapshot(content, $"C:\\{content}"))]);
        var batch = await runtime.Commit.CommitAsync(Request(
            snapshot,
            expectedWorkspace: null,
            CreateCapture(sourceId, content, HistoryWorkspaceStore.MissingRevision, null)));
        return (snapshot, sourceId, batch);
    }

    private async Task<HistoryRuntime> CreateRuntimeAsync(string repositoryName = "repository")
    {
        var repository = new FileHistoryRepository(
            _configId,
            new HistoryRepositoryPaths(Path.Combine(_root, repositoryName)));
        var runtime = new HistoryRuntime(repository);
        await runtime.InitializeAsync();
        return runtime;
    }

    private HistoryCommitRequest Request(
        HistoryConfigSnapshot snapshot,
        HistoryWorkspace? expectedWorkspace,
        params SourceCaptureResult[] results)
    {
        var now = DateTimeOffset.UtcNow;
        return new HistoryCommitRequest(
            snapshot,
            new HistoryBackupInvocation(
                RunId.New(), now.AddSeconds(-1), now, BackupInvocationKind.Manual,
                HistoryProvenance.Native("test")),
            expectedWorkspace,
            results);
    }

    private SourceCaptureResult CreateCapture(
        SourceId sourceId,
        string content,
        long expectedRevision,
        VersionId? expectedBase)
    {
        var path = Path.Combine(_root, $"payload-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var bytes = File.ReadAllBytes(path);
        var representationId = RepresentationId.New();
        var fingerprint = "state-" + content;
        return new SourceCaptureResult(
            sourceId,
            SourceCaptureOutcome.Captured,
            CaptureScope.FullSource,
            fingerprint,
            null,
            new RepresentationCandidate(
                representationId, RepresentationKind.CoreFull, "7z", [], MaterializationFidelity.Exact,
                null, fingerprint, null),
            new LocalReplicaCandidate(
                LocalReplicaId.New(), representationId, LocalReplicaLocator.ControlledAbsolute(path),
                CapturePayloadState.VerifiedFinal, DateTimeOffset.UtcNow),
            new CapturePayloadCandidate(
                path, CapturePayloadState.VerifiedFinal, bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            expectedRevision,
            expectedBase,
            null,
            []);
    }

    private async Task CommitFactsAsync(HistoryRuntime runtime, params object[] facts)
    {
        var pack = new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            facts.Select(fact => _codec.CreateObject(fact)));
        await runtime.Repository.CommitAsync(pack);
    }

    private static HistoryAnnotationUpdate Annotation(
        HistoryAnnotationTarget target,
        HistoryAnnotationKind kind,
        string value,
        DateTimeOffset createdAtUtc)
        => new(AnnotationUpdateId.New(), target, kind, [], value, createdAtUtc);

    private static MaterializationPolicyUpdate Policy(
        VersionId versionId,
        MaterializationPolicyState state,
        IEnumerable<MaterializationPolicyUpdateId> parents)
        => new(MaterializationPolicyUpdateId.New(), versionId, parents, state, DateTimeOffset.UtcNow, "test");
}
