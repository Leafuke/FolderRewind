using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryCommitCoordinatorTests
{
    private string _root = null!;
    private HistoryConfigId _configId;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindCommitCoordinatorTests", Guid.NewGuid().ToString("N"));
        _configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task FirstCaptureCommitsAllAuthoritativeFactsInOnePack()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var capture = CreateCapture(sourceId, "first", -1, null);

        var batch = await runtime.Commit.CommitAsync(Request(snapshot, null, capture));

        Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
        Assert.HasCount(5, batch.Pack.Objects);
        CollectionAssert.AreEquivalent(
            new[]
            {
                HistoryObjectKinds.BackupRun,
                HistoryObjectKinds.BranchUpdate,
                HistoryObjectKinds.ConfigurationCheckpoint,
                HistoryObjectKinds.SourceVersion,
                HistoryObjectKinds.VersionRepresentation
            },
            batch.Pack.Objects.Select(item => item.Kind).ToArray());
        Assert.HasCount(1, batch.NewVersions);
        Assert.HasCount(1, batch.NewRepresentations);
        Assert.IsNotNull(batch.NewCheckpoint);
        Assert.IsTrue(batch.NewCheckpoint.IsComplete);
        Assert.AreEqual(BranchUpdateReason.Created, batch.NewBranchUpdate!.Reason);
        Assert.AreEqual(batch.NewCheckpoint.CheckpointId, batch.Run.ResultCheckpointId);
        Assert.IsTrue(batch.IndexRefreshSucceeded);

        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(0, workspace.StateRevision);
        Assert.AreEqual(batch.NewBranchUpdate.UpdateId, workspace.ActiveBranchUpdateId);
        Assert.AreEqual(batch.NewVersions[0].VersionId, workspace.SourceBaselines[0].BaseVersionId);
        Assert.AreEqual(WorkspaceBaselineRelation.Exact, workspace.SourceBaselines[0].Relation);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!;
        Assert.AreEqual(0, catalog.CatalogRevision);
        Assert.AreEqual(capture.LocalReplicaCandidate!.LocalReplicaId, catalog.Entries.Single().LocalReplicaId);
        Assert.AreEqual(HistoryRuntimeHealth.Ready, runtime.Health);
    }

    [TestMethod]
    public async Task ExactWorkspaceBaselineBecomesVersionParentAndBranchUpdateHasOnlyRefParent()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;

        var second = await runtime.Commit.CommitAsync(Request(
            snapshot,
            workspace,
            CreateCapture(sourceId, "second", workspace.StateRevision, first.NewVersions[0].VersionId)));

        CollectionAssert.AreEqual(
            new[] { first.NewVersions[0].VersionId },
            second.NewVersions[0].ParentVersionIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.NewBranchUpdate!.UpdateId },
            second.NewBranchUpdate!.ParentUpdateIds.ToArray());
        Assert.AreEqual(BranchUpdateReason.Backup, second.NewBranchUpdate.Reason);
    }

    [TestMethod]
    public async Task UnbornBranchFirstBackupTargetsCheckpointAndParentsInitializationUpdate()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var branchId = BranchId.New();
        var unborn = new BranchUpdate(
            BranchUpdateId.New(),
            branchId,
            [],
            "prepared",
            targetCheckpointId: null,
            isDeleted: false,
            DateTimeOffset.UtcNow,
            BranchUpdateReason.Created);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            [codec.CreateObject(unborn)]));
        var workspace = new HistoryWorkspace(
            _configId,
            0,
            branchId,
            unborn.UpdateId,
            [new WorkspaceSourceBaseline(sourceId, null, WorkspaceBaselineRelation.Unknown)]);
        await runtime.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);

        var committed = await runtime.Commit.CommitAsync(Request(
            snapshot,
            workspace,
            CreateCapture(sourceId, "first", workspace.StateRevision, null)));

        Assert.AreEqual(branchId, committed.NewBranchUpdate!.BranchId);
        Assert.AreEqual("prepared", committed.NewBranchUpdate.Name);
        Assert.AreEqual(unborn.UpdateId, committed.NewBranchUpdate.ParentUpdateIds.Single());
        Assert.AreEqual(committed.NewCheckpoint!.CheckpointId, committed.NewBranchUpdate.TargetCheckpointId);
    }

    [TestMethod]
    public async Task NoChangeCommitsOnlyRunAndDoesNotAdvanceWorkspaceOrBranch()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var before = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var noChange = SourceCaptureResult.NoChanges(
            sourceId,
            CaptureScope.FullSource,
            before.StateRevision,
            first.NewVersions[0].VersionId,
            "state-first");

        var second = await runtime.Commit.CommitAsync(Request(snapshot, before, noChange));

        Assert.HasCount(1, second.Pack.Objects);
        Assert.AreEqual(HistoryObjectKinds.BackupRun, second.Pack.Objects[0].Kind);
        Assert.AreEqual(BackupRunOutcome.NoChange, second.Run.Outcome);
        Assert.AreEqual(first.NewCheckpoint!.CheckpointId, second.Run.ResultCheckpointId);
        Assert.IsNull(second.NewCheckpoint);
        Assert.IsNull(second.NewBranchUpdate);
        var after = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(before.StateRevision, after.StateRevision);
        Assert.AreEqual(before.ActiveBranchUpdateId, after.ActiveBranchUpdateId);
    }

    [TestMethod]
    public async Task PartialRunCarriesForwardFailedSourceAndStillAdvancesBranch()
    {
        await using var runtime = await CreateRuntimeAsync();
        var firstSource = SourceId.New();
        var secondSource = SourceId.New();
        var snapshot = Snapshot(Source(firstSource, "source-a"), Source(secondSource, "source-b"));
        var initial = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(firstSource, "first-a", -1, null),
            CreateCapture(secondSource, "first-b", -1, null)));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var firstBaseline = workspace.SourceBaselines.Single(item => item.SourceId == firstSource).BaseVersionId;
        var secondBaseline = workspace.SourceBaselines.Single(item => item.SourceId == secondSource).BaseVersionId;
        var failed = SourceCaptureResult.Failed(
            secondSource,
            CaptureScope.FullSource,
            "source unavailable during capture",
            workspace.StateRevision,
            secondBaseline);

        var partial = await runtime.Commit.CommitAsync(Request(
            snapshot,
            workspace,
            CreateCapture(firstSource, "second-a", workspace.StateRevision, firstBaseline),
            failed));

        Assert.AreEqual(BackupRunOutcome.Partial, partial.Run.Outcome);
        Assert.IsNotNull(partial.NewCheckpoint);
        Assert.IsTrue(partial.NewCheckpoint.IsComplete);
        var failedProjection = partial.NewCheckpoint.Sources.Single(item => item.SourceId == secondSource);
        Assert.AreEqual(CheckpointSourceDisposition.Failed, failedProjection.Disposition);
        Assert.AreEqual(secondBaseline, failedProjection.VersionId);
        Assert.AreEqual(initial.NewBranchUpdate!.UpdateId, partial.NewBranchUpdate!.ParentUpdateIds.Single());
    }

    [TestMethod]
    public async Task SingleSourceBackupCarriesForwardEveryOtherRosterSource()
    {
        await using var runtime = await CreateRuntimeAsync();
        var selectedSource = SourceId.New();
        var otherSource = SourceId.New();
        var snapshot = Snapshot(Source(selectedSource, "selected"), Source(otherSource, "other"));
        _ = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(selectedSource, "first-selected", -1, null),
            CreateCapture(otherSource, "first-other", -1, null)));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var selectedBase = workspace.SourceBaselines.Single(item => item.SourceId == selectedSource).BaseVersionId;
        var otherBase = workspace.SourceBaselines.Single(item => item.SourceId == otherSource).BaseVersionId;

        var committed = await runtime.Commit.CommitAsync(Request(
            snapshot,
            workspace,
            CreateCapture(selectedSource, "second-selected", workspace.StateRevision, selectedBase)));

        var carried = committed.NewCheckpoint!.Sources.Single(item => item.SourceId == otherSource);
        Assert.AreEqual(CheckpointSourceDisposition.CarriedForward, carried.Disposition);
        Assert.AreEqual(otherBase, carried.VersionId);
        Assert.AreEqual(
            BackupRunSourceOutcome.CarriedForward,
            committed.Run.SourceResults.Single(item => item.SourceId == otherSource).Outcome);
    }

    [TestMethod]
    public async Task FirstSingleSourceBackupCreatesPartialRosterCheckpoint()
    {
        await using var runtime = await CreateRuntimeAsync();
        var selectedSource = SourceId.New();
        var unknownSource = SourceId.New();
        var snapshot = Snapshot(Source(selectedSource, "selected"), Source(unknownSource, "unknown"));

        var committed = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(selectedSource, "selected", HistoryWorkspaceStore.MissingRevision, null)));

        Assert.IsNotNull(committed.NewCheckpoint);
        Assert.IsFalse(committed.NewCheckpoint.IsComplete);
        Assert.AreEqual(BackupRunOutcome.Partial, committed.Run.Outcome);
        var unknown = committed.NewCheckpoint.Sources.Single(item => item.SourceId == unknownSource);
        Assert.IsNull(unknown.VersionId);
        Assert.AreEqual(CheckpointSourceDisposition.CarriedForward, unknown.Disposition);
    }

    [TestMethod]
    public async Task BackupAfterHistoricalCheckoutForksSourceDagWithoutAutoCreatingBranch()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var workspaceOne = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var second = await runtime.Commit.CommitAsync(Request(
            snapshot,
            workspaceOne,
            CreateCapture(sourceId, "second", workspaceOne.StateRevision, first.NewVersions[0].VersionId)));
        var workspaceTwo = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var restoredWorkspace = new HistoryWorkspace(
            _configId,
            workspaceTwo.StateRevision + 1,
            workspaceTwo.ActiveBranchId,
            workspaceTwo.ActiveBranchUpdateId,
            [new WorkspaceSourceBaseline(sourceId, first.NewVersions[0].VersionId, WorkspaceBaselineRelation.Exact)]);
        await runtime.WorkspaceStore.SaveAsync(restoredWorkspace, workspaceTwo.StateRevision);

        var fork = await runtime.Commit.CommitAsync(Request(
            snapshot,
            restoredWorkspace,
            CreateCapture(sourceId, "fork", restoredWorkspace.StateRevision, first.NewVersions[0].VersionId)));

        Assert.AreEqual(restoredWorkspace.ActiveBranchId, fork.NewBranchUpdate!.BranchId);
        Assert.AreEqual(BranchUpdateReason.BackupFromHistoricalState, fork.NewBranchUpdate.Reason);
        Assert.AreEqual(second.NewBranchUpdate!.UpdateId, fork.NewBranchUpdate.ParentUpdateIds.Single());
        Assert.AreEqual(first.NewVersions[0].VersionId, fork.NewVersions[0].ParentVersionIds.Single());
        Assert.AreNotEqual(second.NewVersions[0].VersionId, fork.NewVersions[0].ParentVersionIds.Single());
    }

    [TestMethod]
    public async Task UnknownWorkspaceRelationCreatesParentlessVersion()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var current = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var unknown = new HistoryWorkspace(
            _configId,
            current.StateRevision + 1,
            current.ActiveBranchId,
            current.ActiveBranchUpdateId,
            [new WorkspaceSourceBaseline(sourceId, first.NewVersions[0].VersionId, WorkspaceBaselineRelation.Unknown)]);
        await runtime.WorkspaceStore.SaveAsync(unknown, current.StateRevision);

        var committed = await runtime.Commit.CommitAsync(Request(
            snapshot,
            unknown,
            CreateCapture(sourceId, "unknown-base", unknown.StateRevision, first.NewVersions[0].VersionId)));

        Assert.IsEmpty(committed.NewVersions[0].ParentVersionIds);
    }

    [TestMethod]
    public async Task WorkspaceConflictCommitsNoPackAndInvokesCaptureCleanup()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        _ = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var stalePayload = Path.Combine(_root, "payload-stale.bin");
        var cleanup = new DeleteFileCleanup(stalePayload);
        var stale = CreateCapture(sourceId, "stale", -1, null, stalePayload, cleanup);

        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(
            () => runtime.Commit.CommitAsync(Request(snapshot, null, stale)));

        Assert.IsTrue(cleanup.WasCalled);
        Assert.IsFalse(File.Exists(stalePayload));
        Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task FailedRunWithoutReliableVersionsCommitsOnlyActivityFact()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var failed = SourceCaptureResult.Failed(
            sourceId,
            CaptureScope.FullSource,
            "capture failed",
            HistoryWorkspaceStore.MissingRevision,
            expectedBaseVersionId: null);

        var committed = await runtime.Commit.CommitAsync(Request(snapshot, null, failed));

        Assert.HasCount(1, committed.Pack.Objects);
        Assert.AreEqual(HistoryObjectKinds.BackupRun, committed.Pack.Objects[0].Kind);
        Assert.AreEqual(BackupRunOutcome.Failed, committed.Run.Outcome);
        Assert.IsNull(committed.NewCheckpoint);
        Assert.AreEqual(DeviceLocalStateStatus.Missing, (await runtime.WorkspaceStore.LoadAsync()).Status);
    }

    [TestMethod]
    public async Task ChangedMaterializationPolicyTipsRejectPreparedCapture()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var policy = new MaterializationPolicyUpdate(
            MaterializationPolicyUpdateId.New(),
            first.NewVersions[0].VersionId,
            [],
            MaterializationPolicyState.Released,
            DateTimeOffset.UtcNow,
            "concurrent release");
        var codec = new HistoryPackCodec();
        var policyPack = new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            [codec.CreateObject(policy)]);
        await runtime.Repository.CommitAsync(policyPack);
        var payload = Path.Combine(_root, "policy-conflict.bin");
        var cleanup = new DeleteFileCleanup(payload);
        var capture = CreateCapture(
            sourceId,
            "stale-policy",
            workspace.StateRevision,
            first.NewVersions[0].VersionId,
            payload,
            cleanup);

        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(
            () => runtime.Commit.CommitAsync(Request(snapshot, workspace, capture)));

        Assert.IsTrue(cleanup.WasCalled);
        Assert.HasCount(2, await runtime.Repository.ReadAllPacksAsync());
    }

    private async Task<HistoryRuntime> CreateRuntimeAsync()
    {
        var repository = new FileHistoryRepository(
            _configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "repository")));
        var runtime = new HistoryRuntime(repository);
        await runtime.InitializeAsync();
        return runtime;
    }

    private HistoryConfigSnapshot Snapshot(params HistoryConfigSourceSnapshot[] sources)
        => new(_configId, sources, "main");

    private static HistoryConfigSourceSnapshot Source(SourceId sourceId, string displayName)
        => new(sourceId, new SourceDescriptorSnapshot(displayName, $"C:\\{displayName}"));

    private HistoryCommitRequest Request(
        HistoryConfigSnapshot snapshot,
        HistoryWorkspace? workspace,
        params SourceCaptureResult[] captures)
    {
        var now = DateTimeOffset.UtcNow;
        return new HistoryCommitRequest(
            snapshot,
            new HistoryBackupInvocation(
                RunId.New(),
                now.AddSeconds(-1),
                now,
                BackupInvocationKind.Manual,
                HistoryProvenance.Native("test-device")),
            workspace,
            captures);
    }

    private SourceCaptureResult CreateCapture(
        SourceId sourceId,
        string content,
        long expectedRevision,
        VersionId? expectedBaseVersionId,
        string? payloadPath = null,
        ICaptureCleanupHandle? cleanup = null)
    {
        payloadPath ??= Path.Combine(_root, $"payload-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        File.WriteAllText(payloadPath, content);
        var bytes = File.ReadAllBytes(payloadPath);
        var representationId = RepresentationId.New();
        var fingerprint = $"state-{content}";
        return new SourceCaptureResult(
            sourceId,
            SourceCaptureOutcome.Captured,
            CaptureScope.FullSource,
            fingerprint,
            existingVersionId: null,
            new RepresentationCandidate(
                representationId,
                RepresentationKind.CoreFull,
                "7z",
                dependencyRepresentationIds: [],
                RestoreStrategy.Exact,
                logicalSha256: null,
                stateFingerprint: fingerprint,
                metadata: null),
            new LocalReplicaCandidate(
                LocalReplicaId.New(),
                representationId,
                LocalReplicaLocator.ControlledAbsolute(payloadPath),
                CapturePayloadState.VerifiedFinal,
                DateTimeOffset.UtcNow),
            new CapturePayloadCandidate(
                payloadPath,
                CapturePayloadState.VerifiedFinal,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            expectedRevision,
            expectedBaseVersionId,
            cleanup,
            diagnostics: []);
    }

    private sealed class DeleteFileCleanup(string path) : ICaptureCleanupHandle
    {
        public bool WasCalled { get; private set; }

        public ValueTask CleanupAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            if (File.Exists(path)) File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
