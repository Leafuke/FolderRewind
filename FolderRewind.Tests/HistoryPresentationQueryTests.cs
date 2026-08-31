using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryPresentationQueryTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindPresentationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task TimelineKeepsParentlessAndDivergentVersionsWhileMultiTipCommandsAreDisabled()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var first = Version(configId, sourceId, "first");
        var second = Version(configId, sourceId, "second");
        var checkpointOne = Checkpoint(configId, sourceId, first);
        var duplicateCheckpointForFirst = Checkpoint(configId, sourceId, first);
        var checkpointTwo = Checkpoint(configId, sourceId, second);
        var safetySnapshot = new SafetySnapshot(
            SafetySnapshotId.New(),
            checkpointOne.CheckpointId,
            DateTimeOffset.UtcNow,
            SafetySnapshotReason.BeforeCheckout);
        var branchId = BranchId.New();
        var tipOne = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpointOne.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var tipTwo = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpointTwo.CheckpointId, false,
            DateTimeOffset.UtcNow.AddTicks(1), BranchUpdateReason.Backup);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[]
            {
                first, second, checkpointOne, duplicateCheckpointForFirst, checkpointTwo,
                tipOne, tipTwo, safetySnapshot
            }
                .Select(item => codec.CreateObject(item))));

        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();

        CollectionAssert.AreEquivalent(
            new[] { first.VersionId, second.VersionId },
            snapshot.Timeline.Select(item => item.VersionId).ToArray());
        Assert.IsTrue(snapshot.Timeline.All(item => item.ParentVersionIds.IsEmpty));
        var branch = snapshot.Branches.Single();
        Assert.IsTrue(branch.IsMultiTip);
        Assert.IsFalse(branch.CanRename);
        Assert.IsFalse(branch.CanDelete);
        Assert.IsTrue(branch.HasCheckoutTarget);
        var firstTimeline = snapshot.Timeline.Single(item => item.VersionId == first.VersionId);
        Assert.AreEqual(2, firstTimeline.BranchableCheckpointCount);
        Assert.IsNull(firstTimeline.BranchableCheckpointId);
        var secondTimeline = snapshot.Timeline.Single(item => item.VersionId == second.VersionId);
        Assert.AreEqual(checkpointTwo.CheckpointId, secondTimeline.BranchableCheckpointId);
        Assert.AreEqual(safetySnapshot.SnapshotId, snapshot.ActiveSafetySnapshots.Single().Snapshot.SnapshotId);
    }

    [TestMethod]
    public async Task CompletedRunStillReportsPartialCaptureFromItsSourceVersion()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "partial-run-repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var version = new SourceVersion(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.PartialSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("partial", "partial"), null, HistoryProvenance.Native("test"));
        var run = new BackupRun(
            RunId.New(), configId, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow,
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(sourceId, BackupRunSourceOutcome.Captured, version.VersionId, [])],
            resultCheckpointId: null,
            diagnostics: []);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version), codec.CreateObject(run)]));

        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();

        Assert.IsTrue(snapshot.Runs.Single().HasPartialCapture);
    }

    [TestMethod]
    public async Task BranchMembershipAndActiveBranchAreProjectedForTimelineFiltering()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "branch-membership-repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var shared = Version(configId, sourceId, "shared");
        var mainOnly = Version(configId, sourceId, "main-only");
        var featureOnly = Version(configId, sourceId, "feature-only");
        var sharedCheckpoint = Checkpoint(configId, sourceId, shared);
        var mainCheckpoint = Checkpoint(configId, sourceId, mainOnly);
        var featureCheckpoint = Checkpoint(configId, sourceId, featureOnly);
        var mainId = BranchId.New();
        var featureId = BranchId.New();
        var mainRoot = new BranchUpdate(
            BranchUpdateId.New(), mainId, [], "main", sharedCheckpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var mainTip = new BranchUpdate(
            BranchUpdateId.New(), mainId, [mainRoot.UpdateId], "main", mainCheckpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddTicks(1), BranchUpdateReason.Backup);
        var featureRoot = new BranchUpdate(
            BranchUpdateId.New(), featureId, [], "feature", sharedCheckpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddTicks(2), BranchUpdateReason.Created);
        var featureTip = new BranchUpdate(
            BranchUpdateId.New(), featureId, [featureRoot.UpdateId], "feature", featureCheckpoint.CheckpointId, false,
            DateTimeOffset.UtcNow.AddTicks(3), BranchUpdateReason.Backup);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[]
            {
                shared, mainOnly, featureOnly,
                sharedCheckpoint, mainCheckpoint, featureCheckpoint,
                mainRoot, mainTip, featureRoot, featureTip
            }.Select(item => codec.CreateObject(item))));
        await runtime.WorkspaceStore.SaveAsync(
            new FolderRewind.History.LocalState.HistoryWorkspace(
                configId,
                0,
                featureId,
                featureTip.UpdateId,
                [new FolderRewind.History.LocalState.WorkspaceSourceBaseline(
                    sourceId,
                    featureOnly.VersionId,
                    FolderRewind.History.LocalState.WorkspaceBaselineRelation.Exact)]),
            FolderRewind.History.LocalState.HistoryWorkspaceStore.MissingRevision);

        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();

        Assert.AreEqual(featureId, snapshot.ActiveBranchId);
        var activeBranch = snapshot.Branches.Single(branch => branch.BranchId == featureId);
        Assert.IsTrue(activeBranch.IsActive);
        Assert.IsTrue(activeBranch.IsWorkspaceAnchoredAtTip);
        CollectionAssert.AreEquivalent(
            new[] { mainId, featureId },
            snapshot.Timeline.Single(item => item.VersionId == shared.VersionId).BranchIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { mainId },
            snapshot.Timeline.Single(item => item.VersionId == mainOnly.VersionId).BranchIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { featureId },
            snapshot.Timeline.Single(item => item.VersionId == featureOnly.VersionId).BranchIds.ToArray());

        await runtime.WorkspaceStore.SaveAsync(
            new FolderRewind.History.LocalState.HistoryWorkspace(
                configId,
                1,
                featureId,
                featureRoot.UpdateId,
                [new FolderRewind.History.LocalState.WorkspaceSourceBaseline(
                    sourceId,
                    shared.VersionId,
                    FolderRewind.History.LocalState.WorkspaceBaselineRelation.Exact)]),
            expectedRevision: 0);
        var staleAnchorSnapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();
        Assert.IsFalse(staleAnchorSnapshot.Branches
            .Single(branch => branch.BranchId == featureId)
            .IsWorkspaceAnchoredAtTip);
    }

    private static SourceVersion Version(HistoryConfigId configId, SourceId sourceId, string name)
        => new(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot(name, name), name, HistoryProvenance.Native("test"));

    private static ConfigurationCheckpoint Checkpoint(
        HistoryConfigId configId,
        SourceId sourceId,
        SourceVersion version)
        => new(
            CheckpointId.New(), configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, version.SourceDescriptorSnapshot, version.VersionId,
                CheckpointSourceDisposition.CarriedForward)]);
}
