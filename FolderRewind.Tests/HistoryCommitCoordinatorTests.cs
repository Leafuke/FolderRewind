using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.History.Cloud;
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
    public async Task CloudStatusReadersCanPollWhileWorkerAtomicallyReplacesJournal()
    {
        await using var runtime = await CreateRuntimeAsync();
        runtime.CloudJobs.ConfigureAdmission(new("profile", "revision", "binding", "remote:backup", _configId.Value));
        var source = SourceId.New();
        await runtime.Commit.CommitAsync(Request(Snapshot(Source(source, "source")), null, CreateCapture(source, "first", -1, null)));
        var initial = runtime.CloudJobs.Read().Jobs.Single();
        using var cancellation = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            long revision = 0;
            do
            {
                var observed = runtime.CloudJobs.Read();
                Assert.IsGreaterThanOrEqualTo(revision, observed.Revision);
                revision = observed.Revision;
                Assert.AreEqual(initial.JobId, observed.Jobs.Single().JobId);
                await Task.Yield();
            } while (!cancellation.IsCancellationRequested);
        })).ToArray();
        try
        {
            for (var attempt = 1; attempt <= 100; attempt++)
            {
                await using var gate = await runtime.MutationGate.EnterAsync(default);
                runtime.CloudJobs.Update(initial with { Attempt = attempt, Diagnostic = "polling regression" });
            }
        }
        finally { cancellation.Cancel(); await Task.WhenAll(readers); }
        Assert.AreEqual(100, runtime.CloudJobs.Read().Jobs.Single().Attempt);
        Assert.IsFalse(File.Exists(runtime.CloudJobs.JournalPath + ".attention"));
    }

    [TestMethod]
    public async Task DurableParticipantFailureReleasesOperationOwnershipBeforeRepeatedRecovery()
    {
        await using var runtime = await CreateRuntimeAsync();
        var source = SourceId.New(); var participant = new FailingParticipant();
        var capture = CreateCapture(source, "first", -1, null) with { CommitParticipant = participant };
        await Assert.ThrowsExactlyAsync<HistoryCommitRecoveryRequiredException>(() => runtime.Commit.CommitAsync(Request(Snapshot(Source(source, "source")), null, capture)));
        Assert.IsTrue(participant.Ended);
        Assert.IsFalse(participant.Aborted);
        Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
    }
    private sealed class FailingParticipant : ICaptureCommitParticipant
    {
        public bool Ended { get; private set; }
        public bool Aborted { get; private set; }
        public Task PrepareCommitAsync(PackId pack, HistoryTransactionId transaction, CancellationToken token) => Task.CompletedTask;
        public Task HistoryCommittedAsync(CancellationToken token) => throw new IOException("injected participant failure");
        public Task CompleteCommitAsync(CancellationToken token) => Task.CompletedTask;
        public Task AbortCommitAsync(CancellationToken token) { Aborted = true; return Task.CompletedTask; }
        public void EndOperation() => Ended = true;
    }

    [TestMethod]
    [DataRow("before-prepared")]
    [DataRow("prepared")]
    [DataRow("before-activate")]
    [DataRow("before-journal-write")]
    [DataRow("journal-and-attention-write")]
    public async Task CloudAdmissionReconcilesCommitGapsBeforeReleasingCleanup(string fault)
    {
        PackId committed;
        var target = new CloudJobTarget("profile", "profile-revision", "binding", "original:/backup", _configId.Value);
        await using (var runtime = await CreateRuntimeAsync())
        {
            runtime.CloudJobs.ConfigureAdmission(target);
            runtime.CloudJobs.Checkpoint = point => { if (point == fault || fault == "journal-and-attention-write" && point is "before-journal-write" or "before-attention-write") throw new IOException("injected cloud journal failure"); };
            var source = SourceId.New();
            var batch = await runtime.Commit.CommitAsync(Request(Snapshot(Source(source, "source")), null, CreateCapture(source, "first", -1, null)));
            committed = batch.Pack.PackId;
            Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
            Assert.ThrowsExactly<InvalidDataException>(() => runtime.CloudJobs.RequireCleanupSafe());
            Assert.IsTrue(File.Exists((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries.Single().Locator.AbsolutePath));
        }
        await using var restarted = await CreateRuntimeAsync();
        var job = restarted.CloudJobs.Read().Jobs.Single();
        Assert.AreEqual(committed, job.PackId);
        Assert.AreEqual(target, job.Target);
        Assert.AreEqual(CloudJobPhase.Pending, job.Phase);
        Assert.AreEqual(CloudProtectionDisposition.Held, job.Protection);
        Assert.HasCount(1, restarted.CloudJobs.ProtectedRoots());
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.CloudJobs.RequireCleanupSafe());
    }

    [TestMethod]
    public async Task CloudAdmissionIncludesReusedVersionAndCancelKeepsProtection()
    {
        await using var runtime = await CreateRuntimeAsync();
        var source = SourceId.New(); var snapshot = Snapshot(Source(source, "source"));
        var first = await runtime.Commit.CommitAsync(Request(snapshot, null, CreateCapture(source, "first", -1, null)));
        runtime.CloudJobs.ConfigureAdmission(new("profile", "revision", "binding", "remote:backup", _configId.Value));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var unchanged = SourceCaptureResult.NoChanges(source, CaptureScope.FullSource, workspace.StateRevision, first.NewVersions.Single().VersionId, "state-first");
        var next = await runtime.Commit.CommitAsync(Request(snapshot, workspace, unchanged));
        Assert.IsEmpty(next.NewRepresentations);
        var job = runtime.CloudJobs.Read().Jobs.Single();
        Assert.AreEqual(first.NewRepresentations.Single().RepresentationId, job.Representations.Single());
        var fingerprint = runtime.CloudJobs.ProtectionFingerprint();
        runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.Canceled });
        Assert.AreNotEqual(fingerprint, runtime.CloudJobs.ProtectionFingerprint());
        Assert.ThrowsExactly<InvalidOperationException>(() => runtime.CloudJobs.RequireCleanupSafe());
        runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.Canceled, Protection = CloudProtectionDisposition.Abandoned });
        runtime.CloudJobs.RequireCleanupSafe();
    }

    [TestMethod]
    public async Task MissingJournalKeepsIndependentProtectionAndNeedsExplicitBaselineDisposition()
    {
        await using var runtime = await CreateRuntimeAsync();
        var target = new CloudJobTarget("profile", "revision", "binding", "remote:/original", _configId.Value);
        runtime.CloudJobs.ConfigureAdmission(target);
        File.Delete(runtime.CloudJobs.JournalPath);
        Assert.ThrowsExactly<InvalidDataException>(() => runtime.CloudJobs.RequireCleanupSafe());
        runtime.CloudJobs.InitializeMissingAdmissionFromNow(target);
        Assert.IsTrue(runtime.CloudJobs.Read().UnknownProtectionAbandoned);
        Assert.AreEqual(target, runtime.CloudJobs.Read().Admissions.Single().Target);
        runtime.CloudJobs.RequireCleanupSafe();
    }

    [TestMethod]
    public async Task DamagedCloudJournalBlocksCleanupButAllowsLocalBackup()
    {
        await using var runtime = await CreateRuntimeAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.CloudJobs.JournalPath)!);
        File.WriteAllText(runtime.CloudJobs.JournalPath, "{ invalid cloud evidence");
        var source = SourceId.New();
        var batch = await runtime.Commit.CommitAsync(Request(Snapshot(Source(source, "source")), null, CreateCapture(source, "first", -1, null)));
        Assert.AreEqual(BackupRunOutcome.Completed, batch.Run.Outcome);
        Assert.ThrowsExactly<InvalidDataException>(() => runtime.CloudJobs.RequireCleanupSafe());
        StringAssert.Contains(File.ReadAllText(runtime.CloudJobs.JournalPath), "invalid cloud evidence");
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
                HistoryObjectKinds.SourceCheckpoint,
                HistoryObjectKinds.SourceVersion,
                HistoryObjectKinds.VersionRepresentation
            },
            batch.Pack.Objects.Select(item => item.Kind).ToArray());
        Assert.HasCount(1, batch.NewVersions);
        Assert.HasCount(1, batch.NewRepresentations);
        Assert.IsNotNull(batch.NewCheckpoints.FirstOrDefault()!);
        Assert.IsTrue(batch.NewCheckpoints.FirstOrDefault()!.IsStructurallyComplete);
        Assert.AreEqual(BranchUpdateReason.Created, batch.NewBranchUpdates.FirstOrDefault()!.Reason);
        Assert.AreEqual(batch.NewCheckpoints.FirstOrDefault()!.CheckpointId, batch.Run.SourceResults.FirstOrDefault()?.CheckpointId);
        Assert.IsTrue(batch.IndexRefreshSucceeded);

        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(0, workspace.StateRevision);
        Assert.AreEqual(batch.NewBranchUpdates.FirstOrDefault()!.UpdateId, workspace.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId);
        Assert.AreEqual(batch.NewVersions[0].VersionId, workspace.SourceBaselines[0].BaseVersionId);
        Assert.AreEqual(WorkspaceBaselineRelation.Exact, workspace.SourceBaselines[0].Relation);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!;
        Assert.AreEqual(0, catalog.CatalogRevision);
        Assert.AreEqual(capture.LocalReplicaCandidate!.LocalReplicaId, catalog.Entries.Single().LocalReplicaId);
        Assert.AreEqual(HistoryRuntimeHealth.Ready, runtime.Health);
    }

    [TestMethod]
    public async Task MigratedWorkspaceStartsIndependentFullBackupWithoutInventingAncestry()
    {
        await using var source = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var original = await source.Commit.CommitAsync(Request(snapshot, null, CreateCapture(sourceId, "old", -1, null)));
        var bundle = Path.Combine(_root, "migration.frhistory");
        await new HistoryRepositoryTransferService().ExportAsync(source, bundle);
        var targetDirectory = Path.Combine(_root, "fresh-config");
        await new HistoryRepositoryBatchTransferService(configExists: _ => true).ImportAsync(bundle, targetDirectory);
        await using var target = new HistoryRuntime(new FileHistoryRepository(_configId,
            HistoryRepositoryPaths.ForConfigDirectory(targetDirectory, _configId)));
        await target.InitializeAsync();
        Assert.AreEqual(HistoryRuntimeHealth.LocalStateInitializationRequired, target.Health);
        await new HistoryMigrationInitializationService(target).InitializeAsync([sourceId]);
        var workspace = (await target.WorkspaceStore.LoadAsync()).Value!;
        Assert.IsNull(workspace.GetSourceState(sourceId).BaseVersionId);
        var batch = await target.Commit.CommitAsync(Request(snapshot, workspace, CreateCapture(sourceId, "new", workspace.StateRevision, null)));
        Assert.IsEmpty(batch.NewVersions.Single().ParentVersionIds);
        Assert.IsEmpty(batch.NewRepresentations.Single().DependencyRepresentationIds);
        Assert.AreNotEqual(original.NewBranchUpdates.Single().BranchId, batch.NewBranchUpdates.Single().BranchId);
        Assert.AreNotEqual(original.NewBranchUpdates.Single().Name, batch.NewBranchUpdates.Single().Name);
        Assert.AreEqual(WorkspaceBaselineRelation.Exact, (await target.WorkspaceStore.LoadAsync()).Value!.GetSourceState(sourceId).Relation);
        Assert.AreEqual(HistoryRuntimeHealth.Ready, target.Health);
    }

    [TestMethod]
    public async Task CapturedResultMayOmitStateFingerprintWhenBothFactsAgree()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var payloadPath = Path.Combine(_root, "verified-factory.7z");
        await File.WriteAllTextAsync(payloadPath, "verified archive bytes");
        var capture = VerifiedArchiveCaptureFactory.Create(
            sourceId,
            CaptureScope.FullSource,
            payloadPath,
            RepresentationKind.CoreFull,
            "7z",
            new Dictionary<string, SourceCaptureFileState>
            {
                ["file.txt"] = new(4, DateTime.UnixEpoch)
            },
            baseline: null,
            dependencies: [],
            consecutiveSmartCaptures: 0,
            fidelity: MaterializationFidelity.Exact);

        var batch = await runtime.Commit.CommitAsync(Request(
            Snapshot(Source(sourceId, "source-a")),
            workspace: null,
            capture));

        Assert.IsNull(batch.NewVersions.Single().StateFingerprint);
        Assert.IsNull(batch.NewRepresentations.Single().StateFingerprint);
        Assert.IsTrue(File.Exists(payloadPath), "A durable Commit Pack owns the archive; cleanup must not run.");
    }

    [TestMethod]
    public async Task NativeCaptureCommitsOneVersionAndSurvivesRepositoryRestart()
    {
        var source = SourceId.New(); var version = VersionId.New(); var artifactRoot = Guid.NewGuid();
        await using (var runtime = await CreateRuntimeAsync())
        {
            var payload = CreateCapture(source, "native", -1, null);
            var native = new SourceCaptureResult(source, SourceCaptureOutcome.Captured, CaptureScope.FullSource, payload.StateFingerprint,
                null, new(payload.RepresentationCandidate!.RepresentationId, RepresentationKind.PluginArtifact, "test/native-v2", [],
                    MaterializationFidelity.Exact, null, payload.StateFingerprint,
                    new Dictionary<string, string> { ["artifactRootId"] = artifactRoot.ToString("D"), ["nativeArtifactContractVersion"] = "2", ["incrementalEvidence"] = "2" }),
                payload.LocalReplicaCandidate, payload.PayloadCandidate, -1, null, null, [], effectiveSourceBoundary: payload.EffectiveSourceBoundary)
                { ReservedVersionId = version };
            var batch = await runtime.Commit.CommitAsync(Request(Snapshot(Source(source, "world")), null, native));
            Assert.HasCount(1, batch.NewVersions); Assert.AreEqual(version, batch.NewVersions[0].VersionId);
            Assert.HasCount(1, batch.NewRepresentations); Assert.AreEqual(RepresentationKind.PluginArtifact, batch.NewRepresentations[0].Kind);
            Assert.IsEmpty(batch.NewRepresentations[0].DependencyRepresentationIds);
            Assert.HasCount(1, batch.NewCheckpoints); Assert.HasCount(1, batch.NewBranchUpdates);
            Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
        }
        await using var reopened = await CreateRuntimeAsync();
        Assert.HasCount(1, await reopened.Query.GetAllVersionsAsync());
        Assert.HasCount(1, await reopened.Query.GetAllRepresentationsAsync());
        Assert.HasCount(1, (await reopened.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }

    [TestMethod]
    public async Task IndependentRecoveryPointCommitsSourceCheckpointsWithoutAdvancingBranches()
    {
        await using var runtime = await CreateRuntimeAsync();
        var first = SourceId.New();
        var second = SourceId.New();
        var snapshot = Snapshot(Source(first, "first"), Source(second, "second"));

        var batch = await runtime.Commit.CommitAsync(SafetyRequest(
            snapshot,
            null,
            SafetySnapshotReason.BeforeCheckout,
            CreateCapture(first, "first-state", -1, null),
            CreateCapture(second, "second-state", -1, null)));

        Assert.HasCount(2, batch.NewCheckpoints);
        Assert.IsNull(batch.NewBranchUpdates.FirstOrDefault()!);
        Assert.IsNotNull(batch.NewSafetySnapshot);
        Assert.AreEqual(SafetySnapshotReason.BeforeCheckout, batch.NewSafetySnapshot.Reason);
        Assert.HasCount(2, batch.NewVersions);
        Assert.IsNotNull(batch.UpdatedWorkspace);
        Assert.IsNull(batch.UpdatedWorkspace.SourceBaselines.FirstOrDefault()?.ActiveBranchId);
        Assert.IsNull(batch.UpdatedWorkspace.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId);
        Assert.IsTrue(batch.UpdatedWorkspace.SourceBaselines.All(item =>
            item.Relation == WorkspaceBaselineRelation.Exact));

        var snapshots = new SafetySnapshotService(runtime);
        Assert.HasCount(1, await snapshots.QueryAsync());
        Assert.IsTrue(await snapshots.ReleaseAsync(batch.NewSafetySnapshot.SnapshotId));
        Assert.IsFalse(await snapshots.ReleaseAsync(batch.NewSafetySnapshot.SnapshotId));
        Assert.IsEmpty(await snapshots.QueryAsync());
        Assert.HasCount(1, await snapshots.QueryAsync(activeOnly: false));
    }

    [TestMethod]
    public async Task FinalUnverifiedPayloadIsRejectedBeforePackCommit()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var capture = CreateCapture(
            sourceId,
            "unverified",
            HistoryWorkspaceStore.MissingRevision,
            expectedBaseVersionId: null,
            payloadState: CapturePayloadState.FinalUnverified);

        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => runtime.Commit.CommitAsync(Request(
            Snapshot(Source(sourceId, "source-a")),
            workspace: null,
            capture)));
        Assert.IsEmpty(await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task OperationCommentIsCommittedForRunAndNewSourceVersion()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var request = Request(snapshot, null, CreateCapture(sourceId, "first", -1, null));
        request = new HistoryCommitRequest(
            request.ConfigSnapshot,
            request.Invocation with { Comment = " before update " },
            request.ExpectedWorkspace,
            request.SourceCaptureResults);

        var batch = await runtime.Commit.CommitAsync(request);

        var runComments = await runtime.Query.GetAnnotationUpdatesAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, batch.Run.RunId.Value),
            HistoryAnnotationKind.Comment);
        var versionComments = await runtime.Query.GetAnnotationUpdatesAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, batch.NewVersions.Single().VersionId.Value),
            HistoryAnnotationKind.Comment);
        Assert.AreEqual("before update", runComments.Single().Value);
        Assert.AreEqual("before update", versionComments.Single().Value);
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
            new[] { first.NewBranchUpdates.FirstOrDefault()!.UpdateId },
            second.NewBranchUpdates.FirstOrDefault()!.ParentUpdateIds.ToArray());
        Assert.AreEqual(BranchUpdateReason.Backup, second.NewBranchUpdates.FirstOrDefault()!.Reason);
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
            BranchUpdateReason.Created, sourceId: sourceId);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            [codec.CreateObject(unborn)]));
        var workspace = new HistoryWorkspace(
            _configId,
            0, HistoryFixture.SourceStates([new WorkspaceSourceBaseline(sourceId, null, WorkspaceBaselineRelation.Unknown)], branchId, unborn.UpdateId, null));
        await runtime.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);

        var committed = await runtime.Commit.CommitAsync(Request(
            snapshot,
            workspace,
            CreateCapture(sourceId, "first", workspace.StateRevision, null)));

        Assert.AreEqual(branchId, committed.NewBranchUpdates.FirstOrDefault()!.BranchId);
        Assert.AreEqual("prepared", committed.NewBranchUpdates.FirstOrDefault()!.Name);
        Assert.AreEqual(unborn.UpdateId, committed.NewBranchUpdates.FirstOrDefault()!.ParentUpdateIds.Single());
        Assert.AreEqual(committed.NewCheckpoints.FirstOrDefault()!.CheckpointId, committed.NewBranchUpdates.FirstOrDefault()!.TargetCheckpointId);
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
        Assert.AreEqual(first.NewCheckpoints.FirstOrDefault()!.CheckpointId, second.Run.SourceResults.FirstOrDefault()?.CheckpointId);
        Assert.IsNull(second.NewCheckpoints.FirstOrDefault()!);
        Assert.IsNull(second.NewBranchUpdates.FirstOrDefault()!);
        var after = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(before.StateRevision, after.StateRevision);
        Assert.AreEqual(before.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId, after.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId);
    }

    [TestMethod]
    public async Task BackupExtendsWorkspaceCheckpointAnchor()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var firstWorkspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;

        var second = await runtime.Commit.CommitAsync(Request(
            snapshot,
            firstWorkspace,
            CreateCapture(
                sourceId,
                "second",
                firstWorkspace.StateRevision,
                first.NewVersions.Single().VersionId)));

        Assert.AreEqual(
            first.NewCheckpoints.FirstOrDefault()!.CheckpointId,
            second.NewCheckpoints.FirstOrDefault()!.ParentCheckpointIds.Single());
        Assert.AreEqual(
            second.NewCheckpoints.FirstOrDefault()!.CheckpointId,
            second.UpdatedWorkspace!.SourceBaselines.FirstOrDefault()?.CheckpointAncestryAnchorId);
    }

    [TestMethod]
    public async Task PartialRunPreservesFailedSourceAndAdvancesOnlySuccessfulSource()
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
        Assert.IsNotNull(partial.NewCheckpoints.FirstOrDefault()!);
        Assert.IsTrue(partial.NewCheckpoints.FirstOrDefault()!.IsStructurallyComplete);
        Assert.IsNull(partial.Run.SourceResults.Single(item => item.SourceId == secondSource).VersionId);
        Assert.AreEqual(workspace.GetSourceState(secondSource), partial.UpdatedWorkspace!.GetSourceState(secondSource));
        Assert.AreEqual(initial.NewBranchUpdates.Single(u => u.SourceId == firstSource).UpdateId,
            partial.NewBranchUpdates.Single().ParentUpdateIds.Single());
    }

    [TestMethod]
    public async Task SuccessfulInitialSourceCreatesBranchDespiteAnotherUnavailableSource()
    {
        await using var runtime = await CreateRuntimeAsync();
        var capturedSource = SourceId.New();
        var unavailableSource = SourceId.New();
        var snapshot = Snapshot(
            Source(capturedSource, "captured"),
            Source(unavailableSource, "unavailable"));

        var result = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(capturedSource, "state", -1, null),
            SourceCaptureResult.Unavailable(
                unavailableSource,
                CaptureScope.FullSource,
                "not available")));

        Assert.AreEqual(BackupRunOutcome.Partial, result.Run.Outcome);
        Assert.HasCount(1, result.NewCheckpoints);
        Assert.HasCount(1, result.NewBranchUpdates);
        Assert.AreEqual(capturedSource, result.NewBranchUpdates.Single().SourceId);
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(result.NewVersions.Single().VersionId, workspace.GetSourceState(capturedSource).BaseVersionId);
        Assert.IsNull(workspace.GetSourceState(unavailableSource).ActiveBranchId);
    }

    [TestMethod]
    public async Task BoundaryDriftRequiresSelfContainedRecaptureBeforeBranchAdvance()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var originalBoundary = EffectiveSourceBoundarySnapshot.All;
        var changedBoundary = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["region/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            [],
            false);
        var initialSnapshot = Snapshot(Source(sourceId, "world", originalBoundary));
        _ = await runtime.Commit.CommitAsync(Request(
            initialSnapshot,
            null,
            CreateCapture(sourceId, "first", -1, null, boundary: originalBoundary)));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var changedSnapshot = Snapshot(Source(sourceId, "world", changedBoundary));
        var failed = SourceCaptureResult.Failed(
                sourceId,
                CaptureScope.FullSource,
                "capture failed",
                workspace.StateRevision,
                workspace.SourceBaselines.Single().BaseVersionId)
            .WithEffectiveSourceBoundary(changedBoundary);

        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(
            () => runtime.Commit.CommitAsync(Request(changedSnapshot, workspace, failed)));

        var committed = await runtime.Commit.CommitAsync(Request(
            changedSnapshot,
            workspace,
            CreateCapture(
                sourceId,
                "second",
                workspace.StateRevision,
                workspace.SourceBaselines.Single().BaseVersionId,
                boundary: changedBoundary)));
        Assert.AreEqual(changedBoundary.Fingerprint, committed.NewVersions.Single().EffectiveSourceBoundaryFingerprint);
        Assert.IsEmpty(committed.NewRepresentations.Single().DependencyRepresentationIds);
    }

    [TestMethod]
    public async Task SingleSourceBackupPreservesUnrequestedSourceWorkspace()
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

        Assert.HasCount(1, committed.Run.SourceResults);
        Assert.AreEqual(selectedSource, committed.NewCheckpoints.Single().SourceId);
        Assert.AreEqual(workspace.GetSourceState(otherSource), committed.UpdatedWorkspace!.GetSourceState(otherSource));
    }

    [TestMethod]
    public async Task FirstSingleSourceBackupCreatesCompleteSourceCheckpoint()
    {
        await using var runtime = await CreateRuntimeAsync();
        var selectedSource = SourceId.New();
        var unknownSource = SourceId.New();
        var snapshot = Snapshot(Source(selectedSource, "selected"), Source(unknownSource, "unknown"));

        var committed = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(selectedSource, "selected", HistoryWorkspaceStore.MissingRevision, null)));

        Assert.HasCount(1, committed.NewCheckpoints);
        Assert.AreEqual(selectedSource, committed.NewCheckpoints.Single().SourceId);
        Assert.IsTrue(committed.NewCheckpoints.Single().IsStructurallyComplete);
        Assert.AreEqual(BackupRunOutcome.Completed, committed.Run.Outcome);
        Assert.HasCount(1, committed.NewBranchUpdates);
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
            workspaceTwo.StateRevision + 1, HistoryFixture.SourceStates([new WorkspaceSourceBaseline(sourceId, first.NewVersions[0].VersionId, WorkspaceBaselineRelation.Exact)], workspaceTwo.SourceBaselines.FirstOrDefault()?.ActiveBranchId, workspaceTwo.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId, null));
        await runtime.WorkspaceStore.SaveAsync(restoredWorkspace, workspaceTwo.StateRevision);

        var fork = await runtime.Commit.CommitAsync(Request(
            snapshot,
            restoredWorkspace,
            CreateCapture(sourceId, "fork", restoredWorkspace.StateRevision, first.NewVersions[0].VersionId)));

        Assert.AreEqual(restoredWorkspace.SourceBaselines.FirstOrDefault()?.ActiveBranchId, fork.NewBranchUpdates.FirstOrDefault()!.BranchId);
        Assert.AreEqual(BranchUpdateReason.BackupFromHistoricalState, fork.NewBranchUpdates.FirstOrDefault()!.Reason);
        Assert.AreEqual(second.NewBranchUpdates.FirstOrDefault()!.UpdateId, fork.NewBranchUpdates.FirstOrDefault()!.ParentUpdateIds.Single());
        Assert.AreEqual(first.NewVersions[0].VersionId, fork.NewVersions[0].ParentVersionIds.Single());
        Assert.AreNotEqual(second.NewVersions[0].VersionId, fork.NewVersions[0].ParentVersionIds.Single());
    }

    [TestMethod]
    public async Task CaptureCurrentStateForNewBranchDoesNotAdvanceOldBranch()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));
        var first = await runtime.Commit.CommitAsync(Request(
            snapshot,
            null,
            CreateCapture(sourceId, "first", -1, null)));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var dirtyCapture = CreateCapture(
            sourceId,
            "dirty-working-state",
            workspace.StateRevision,
            first.NewVersions[0].VersionId);

        var created = await runtime.Branches.CaptureCurrentStateForBranchAsync(
            Request(snapshot, workspace, dirtyCapture),
            "experiment");

        Assert.AreNotEqual(first.NewBranchUpdates.FirstOrDefault()!.BranchId, created.NewBranchUpdates.FirstOrDefault()!.BranchId);
        Assert.AreEqual(first.NewBranchUpdates.FirstOrDefault()!.UpdateId, created.NewBranchUpdates.FirstOrDefault()!.ParentUpdateIds.Single());
        Assert.AreEqual(BranchUpdateReason.Created, created.NewBranchUpdates.FirstOrDefault()!.Reason);
        Assert.AreEqual(first.NewVersions[0].VersionId, created.NewVersions[0].ParentVersionIds.Single());
        var oldTips = await runtime.Query.GetBranchTipsAsync(first.NewBranchUpdates.FirstOrDefault()!.BranchId);
        Assert.HasCount(1, oldTips);
        Assert.AreEqual(first.NewBranchUpdates.FirstOrDefault()!.UpdateId, oldTips[0].UpdateId);
        var current = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(created.NewBranchUpdates.FirstOrDefault()!.BranchId, current.SourceBaselines.FirstOrDefault()?.ActiveBranchId);
        Assert.AreEqual(created.NewBranchUpdates.FirstOrDefault()!.UpdateId, current.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId);
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
            current.StateRevision + 1, HistoryFixture.SourceStates([new WorkspaceSourceBaseline(sourceId, first.NewVersions[0].VersionId, WorkspaceBaselineRelation.Unknown)], current.SourceBaselines.FirstOrDefault()?.ActiveBranchId, current.SourceBaselines.FirstOrDefault()?.ActiveBranchUpdateId, null));
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
        Assert.IsNull(committed.NewCheckpoints.FirstOrDefault()!);
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.HasCount(1, workspace.SourceBaselines);
        var state = workspace.GetSourceState(sourceId);
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, state.Relation);
        Assert.IsNull(state.BaseVersionId);
        Assert.IsNull(state.ActiveBranchId);
        Assert.IsNull(state.ActiveBranchUpdateId);
        Assert.IsNull(state.CheckpointAncestryAnchorId);
        Assert.IsEmpty(committed.NewVersions);
        Assert.IsEmpty(committed.NewRepresentations);
        Assert.IsEmpty(committed.NewBranchUpdates);
    }

    [TestMethod]
    public async Task FailedRunPreservesExistingAndUnrequestedSourceStates()
    {
        await using var runtime = await CreateRuntimeAsync();
        var source = SourceId.New(); var other = SourceId.New();
        var snapshot = Snapshot(Source(source, "source"), Source(other, "other"));
        await runtime.Commit.CommitAsync(Request(snapshot, null,
            CreateCapture(source, "one", -1, null), CreateCapture(other, "two", -1, null)));
        var before = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var failed = SourceCaptureResult.Failed(source, CaptureScope.FullSource, "failed",
            before.StateRevision, before.GetSourceState(source).BaseVersionId);
        var result = await runtime.Commit.CommitAsync(Request(snapshot, before, failed));
        Assert.HasCount(1, result.Pack.Objects);
        Assert.AreEqual(HistoryObjectKinds.BackupRun, result.Pack.Objects.Single().Kind);
        Assert.IsTrue(HistoryWorkspace.StateEquals(before, (await runtime.WorkspaceStore.LoadAsync()).Value!));
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

    [TestMethod]
    public async Task PreflightDoesNotReportUnrequestedSourceWhenBoundaryIsUnchanged()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceA = SourceId.New();
        var sourceB = SourceId.New();
        var boundaryA = EffectiveSourceBoundarySnapshot.All;
        var boundaryB = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["region/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            [],
            false);
        var initialSnapshot = Snapshot(
            Source(sourceA, "source-a", boundaryA),
            Source(sourceB, "source-b", boundaryB));

        _ = await runtime.Commit.CommitAsync(Request(
            initialSnapshot,
            null,
            CreateCapture(sourceA, "a1", -1, null, boundary: boundaryA),
            CreateCapture(sourceB, "b1", -1, null, boundary: boundaryB)));

        var nextSnapshot = Snapshot(
            Source(sourceA, "source-a", boundaryA),
            Source(sourceB, "source-b", boundaryB));

        var requirements = await runtime.Commit.FindRequiredBoundaryRecapturesAsync(
            nextSnapshot,
            [sourceA]);

        Assert.IsEmpty(requirements);
    }

    [TestMethod]
    public async Task PreflightIgnoresUnrequestedSourceBoundaryDrift()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceA = SourceId.New();
        var sourceB = SourceId.New();
        var boundaryA = EffectiveSourceBoundarySnapshot.All;
        var boundaryB = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["region/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            [],
            false);
        var initialSnapshot = Snapshot(
            Source(sourceA, "source-a", boundaryA),
            Source(sourceB, "source-b", boundaryB));

        _ = await runtime.Commit.CommitAsync(Request(
            initialSnapshot,
            null,
            CreateCapture(sourceA, "a1", -1, null, boundary: boundaryA),
            CreateCapture(sourceB, "b1", -1, null, boundary: boundaryB)));

        var changedBoundaryB = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.All,
            [],
            EffectiveBoundaryFilterMode.Blacklist,
            ["session.lock"],
            false);
        var driftSnapshot = Snapshot(
            Source(sourceA, "source-a", boundaryA),
            Source(sourceB, "source-b", changedBoundaryB));

        var requirements = await runtime.Commit.FindRequiredBoundaryRecapturesAsync(
            driftSnapshot,
            [sourceA]);

        Assert.IsEmpty(requirements);
    }

    [TestMethod]
    public async Task PreflightDoesNotReportPlannedSourceWhenBoundaryDriftOccurs()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceB = SourceId.New();
        var boundaryB = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["region/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            [],
            false);
        var initialSnapshot = Snapshot(Source(sourceB, "source-b", boundaryB));

        _ = await runtime.Commit.CommitAsync(Request(
            initialSnapshot,
            null,
            CreateCapture(sourceB, "b1", -1, null, boundary: boundaryB)));

        var changedBoundaryB = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.All,
            [],
            EffectiveBoundaryFilterMode.Blacklist,
            ["session.lock"],
            false);
        var driftSnapshot = Snapshot(Source(sourceB, "source-b", changedBoundaryB));

        var requirements = await runtime.Commit.FindRequiredBoundaryRecapturesAsync(
            driftSnapshot,
            [sourceB]);

        Assert.IsEmpty(requirements);
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

    private static HistoryConfigSourceSnapshot Source(
        SourceId sourceId,
        string displayName,
        EffectiveSourceBoundarySnapshot? boundary = null)
        => new(sourceId, new SourceDescriptorSnapshot(displayName, $"C:\\{displayName}"), boundary);

    [TestMethod]
    public async Task ProtectedCaptureAndReusePinInSamePackWithoutRenamingVersion()
    {
        await using var runtime = await CreateRuntimeAsync();
        var id = SourceId.New(); var snapshot = Snapshot(Source(id, "source"));
        var capture = CreateCapture(id, "protected", -1, null);
        var original = Request(snapshot, null, capture);
        var first = await runtime.Commit.CommitAsync(new HistoryCommitRequest(snapshot,
            original.Invocation with { Comment = "original name" }, null, [capture], protect: true));
        var version = first.NewVersions.Single().VersionId;
        var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.Value);
        Assert.HasCount(1, await runtime.Query.GetAnnotationUpdatesAsync(target, HistoryAnnotationKind.Pin));
        Assert.IsTrue(first.Pack.Objects.Any(o => o.Kind == HistoryObjectKinds.HistoryAnnotationUpdate));
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var unchanged = SourceCaptureResult.NoChanges(id, CaptureScope.FullSource, workspace.StateRevision,
            version, "state-protected");
        var next = Request(snapshot, workspace, unchanged);
        var reused = await runtime.Commit.CommitAsync(new HistoryCommitRequest(snapshot,
            next.Invocation with { Comment = "must not rename" }, workspace, [unchanged], protect: true,
            validateProtectedReuse: (_, _) => Task.FromResult(true)));
        Assert.IsEmpty(reused.NewVersions);
        Assert.AreEqual(version, reused.Run.SourceResults.Single().VersionId);
        var projection = HistoryAnnotationProjection.Project(target, await runtime.Query.GetAnnotationUpdatesAsync(target));
        Assert.IsTrue(projection.IsPinned); Assert.AreEqual("original name", projection.EffectiveComment);
    }

    [TestMethod]
    public async Task ProtectedCaptureRejectsFilteredBoundaryBeforeCommit()
    {
        await using var runtime = await CreateRuntimeAsync();
        var id = SourceId.New();
        var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [],
            EffectiveBoundaryFilterMode.Blacklist, ["playerdata"], false);
        var capture = CreateCapture(id, "filtered", -1, null, boundary: boundary);
        var snapshot = Snapshot(Source(id, "source")); var request = Request(snapshot, null, capture);
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => runtime.Commit.CommitAsync(
            new HistoryCommitRequest(snapshot, request.Invocation, null, [capture], protect: true)));
        Assert.IsEmpty(await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task ProtectedReuseRejectsUnavailablePayloadWithoutCommittingPin()
    {
        await using var runtime = await CreateRuntimeAsync();
        var id = SourceId.New(); var snapshot = Snapshot(Source(id, "source"));
        var first = await runtime.Commit.CommitAsync(Request(snapshot, null, CreateCapture(id, "first", -1, null)));
        var version = first.NewVersions.Single().VersionId;
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var unchanged = SourceCaptureResult.NoChanges(id, CaptureScope.FullSource, workspace.StateRevision, version, "state-first");
        var request = Request(snapshot, workspace, unchanged);
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => runtime.Commit.CommitAsync(
            new HistoryCommitRequest(snapshot, request.Invocation, workspace, [unchanged], protect: true,
                validateProtectedReuse: (_, _) => Task.FromResult(false))));
        Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
        Assert.IsEmpty(await runtime.Query.GetAnnotationUpdatesAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.Value), HistoryAnnotationKind.Pin));
    }

    private HistoryCommitRequest Request(
        HistoryConfigSnapshot snapshot,
        HistoryWorkspace? workspace,
        params SourceCaptureResult[] captures)
        => Request(snapshot, workspace, HistoryCommitIntent.AdvanceBranch, captures);

    private HistoryCommitRequest Request(
        HistoryConfigSnapshot snapshot,
        HistoryWorkspace? workspace,
        HistoryCommitIntent intent,
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
            captures,
            intent: intent,
            affectedSourceIds: captures.Select(item => item.SourceId));
    }

    private HistoryCommitRequest SafetyRequest(
        HistoryConfigSnapshot snapshot,
        HistoryWorkspace? workspace,
        SafetySnapshotReason reason,
        params SourceCaptureResult[] captures)
    {
        var now = DateTimeOffset.UtcNow;
        return new HistoryCommitRequest(
            snapshot,
            new HistoryBackupInvocation(
                RunId.New(),
                now.AddSeconds(-1),
                now,
                BackupInvocationKind.Internal,
                HistoryProvenance.Native("test-device")),
            workspace,
            captures,
            intent: HistoryCommitIntent.IndependentRecoveryPoint,
            affectedSourceIds: captures.Select(item => item.SourceId),
            safetySnapshotIntent: new HistorySafetySnapshotIntent(reason));
    }

    [TestMethod]
    public async Task MigratedChangedWorkspaceContinuesOriginalBranchWithSelfContainedFullParent()
    {
        await using var runtime = await CreateRuntimeAsync();
        var source = SourceId.New(); var snapshot = Snapshot(Source(source, "source"));
        var first = await runtime.Commit.CommitAsync(Request(snapshot, null, CreateCapture(source, "original", -1, null)));
        var originalVersion = first.NewVersions.Single().VersionId;
        var originalBranch = first.NewBranchUpdates.Single().BranchId;
        var continuation = new HistoryMigrationContinuationService(runtime, (_, _, _) => Task.FromResult(false));
        var preview = await continuation.PreviewAsync([source]);
        var workspace = await continuation.ApplyAsync(preview, [new(source, _root)], [new(source, preview.Sources.Single().SuggestedUpdateId)]);
        var state = workspace.GetSourceState(source);
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, state.Relation); Assert.IsNull(state.BaseVersionId);
        Assert.IsTrue(state.RequiresFullCapture); Assert.AreEqual(originalBranch, state.ActiveBranchId);
        var noChange = SourceCaptureResult.NoChanges(source, CaptureScope.FullSource, workspace.StateRevision, originalVersion, "state-original");
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => runtime.Commit.CommitAsync(Request(snapshot, workspace, noChange)));
        Assert.IsTrue((await runtime.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).RequiresFullCapture);
        var second = await runtime.Commit.CommitAsync(Request(snapshot, workspace, CreateCapture(source, "current", workspace.StateRevision, null)));
        Assert.AreEqual(originalBranch, second.NewBranchUpdates.Single().BranchId);
        CollectionAssert.AreEqual(new[] { originalVersion }, second.NewVersions.Single().ParentVersionIds.ToArray());
        Assert.IsFalse(second.UpdatedWorkspace!.GetSourceState(source).RequiresFullCapture);
        Assert.AreEqual(WorkspaceBaselineRelation.Exact, second.UpdatedWorkspace.GetSourceState(source).Relation);
    }

    [TestMethod]
    public async Task MigratedExactWorkspaceAdoptsExistingBranchWithoutManufacturingHistory()
    {
        await using var runtime = await CreateRuntimeAsync(); var source = SourceId.New(); var snapshot = Snapshot(Source(source, "source"));
        var first = await runtime.Commit.CommitAsync(Request(snapshot, null, CreateCapture(source, "original", -1, null)));
        var count = (await runtime.Repository.ReadAllPacksAsync()).Count;
        var continuation = new HistoryMigrationContinuationService(runtime, (_, _, _) => Task.FromResult(true));
        var preview = await continuation.PreviewAsync([source]);
        var workspace = await continuation.ApplyAsync(preview, [new(source, _root)], [new(source, preview.Sources.Single().SuggestedUpdateId)]);
        Assert.AreEqual(first.NewVersions.Single().VersionId, workspace.GetSourceState(source).BaseVersionId);
        Assert.IsFalse(workspace.GetSourceState(source).RequiresFullCapture); Assert.HasCount(count, await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task WorkspaceV3RemainsReadableAndDoesNotInventFullCaptureRequirement()
    {
        await using var runtime = await CreateRuntimeAsync(); var source = SourceId.New();
        await runtime.WorkspaceStore.SaveAsync(new(_configId, 0, [new(source, null, WorkspaceBaselineRelation.Unknown)]), -1);
        var path = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "workspace.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"formatVersion\": 4", "\"formatVersion\": 3", StringComparison.Ordinal));
        var load = await runtime.WorkspaceStore.LoadAsync(); Assert.AreEqual(DeviceLocalStateStatus.Valid, load.Status);
        Assert.IsFalse(load.Value!.GetSourceState(source).RequiresFullCapture);
    }

    private SourceCaptureResult CreateCapture(
        SourceId sourceId,
        string content,
        long expectedRevision,
        VersionId? expectedBaseVersionId,
        string? payloadPath = null,
        ICaptureCleanupHandle? cleanup = null,
        CapturePayloadState payloadState = CapturePayloadState.VerifiedFinal,
        EffectiveSourceBoundarySnapshot? boundary = null)
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
                MaterializationFidelity.Exact,
                logicalSha256: null,
                stateFingerprint: fingerprint,
                metadata: null),
            new LocalReplicaCandidate(
                LocalReplicaId.New(),
                representationId,
                LocalReplicaLocator.ControlledAbsolute(payloadPath),
                payloadState,
                DateTimeOffset.UtcNow),
            new CapturePayloadCandidate(
                payloadPath,
                payloadState,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            expectedRevision,
            expectedBaseVersionId,
            cleanup,
            diagnostics: [],
            effectiveSourceBoundary: boundary);
    }

    [TestMethod]
    public async Task PreflightThrowsWhenWorkspaceIsCorrupt()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));

        // Corrupt workspace file by writing non-json text directly
        var workspacePath = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
        await File.WriteAllTextAsync(workspacePath, "{ not-valid-json");

        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() =>
            runtime.Commit.FindRequiredBoundaryRecapturesAsync(snapshot, plannedCaptureSources: []));
    }

    [TestMethod]
    public async Task PreflightThrowsWhenBaselineVersionIsMissing()
    {
        await using var runtime = await CreateRuntimeAsync();
        var sourceId = SourceId.New();
        var snapshot = Snapshot(Source(sourceId, "source-a"));

        // Save a workspace referencing a nonexistent version id
        var missingVersionId = VersionId.New();
        var workspace = new HistoryWorkspace(
            _configId,
            0, HistoryFixture.SourceStates([new WorkspaceSourceBaseline(sourceId, missingVersionId, WorkspaceBaselineRelation.Exact)], BranchId.New(), BranchUpdateId.New(), null));
        await runtime.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);

        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() =>
            runtime.Commit.FindRequiredBoundaryRecapturesAsync(snapshot, plannedCaptureSources: [sourceId]));
        Assert.IsEmpty(await runtime.Commit.FindRequiredBoundaryRecapturesAsync(snapshot, []));
        var other = SourceId.New();
        Assert.IsEmpty(await runtime.Commit.FindRequiredBoundaryRecapturesAsync(
            Snapshot(Source(sourceId, "source-a"), Source(other, "source-b")), [other]));
        Assert.IsEmpty(await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task PreflightAndCommitRejectBaselineOwnedByAnotherSource()
    {
        await using var runtime = await CreateRuntimeAsync();
        var owner = SourceId.New(); var requested = SourceId.New();
        var first = await runtime.Commit.CommitAsync(Request(Snapshot(Source(owner, "owner")), null,
            CreateCapture(owner, "first", -1, null)));
        var before = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var invalid = before.WithSourceStates([new(requested, first.NewVersions.Single().VersionId,
            WorkspaceBaselineRelation.Unknown)]);
        await runtime.WorkspaceStore.SaveAsync(invalid, before.StateRevision);
        var snapshot = Snapshot(Source(requested, "requested"));
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() =>
            runtime.Commit.FindRequiredBoundaryRecapturesAsync(snapshot, [requested]));
        var cleanup = new DeleteFileCleanup(Path.Combine(_root, "cross-source.bin"));
        var capture = CreateCapture(requested, "captured", invalid.StateRevision,
            first.NewVersions.Single().VersionId, Path.Combine(_root, "cross-source.bin"), cleanup);
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() =>
            runtime.Commit.CommitAsync(Request(snapshot, invalid, capture)));
        Assert.IsTrue(cleanup.WasCalled);
        Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task CommitRechecksBaselineAfterSuccessfulPreflight()
    {
        await using var runtime = await CreateRuntimeAsync();
        var source = SourceId.New(); var snapshot = Snapshot(Source(source, "source"));
        Assert.IsEmpty(await runtime.Commit.FindRequiredBoundaryRecapturesAsync(snapshot, [source]));
        var workspace = new HistoryWorkspace(_configId, 0,
            [new(source, VersionId.New(), WorkspaceBaselineRelation.Unknown)]);
        await runtime.WorkspaceStore.SaveAsync(workspace, -1);
        var path = Path.Combine(_root, "missing-baseline.bin");
        var cleanup = new DeleteFileCleanup(path);
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => runtime.Commit.CommitAsync(
            Request(snapshot, workspace, CreateCapture(source, "new", 0,
                workspace.GetSourceState(source).BaseVersionId, path, cleanup))));
        Assert.IsTrue(cleanup.WasCalled);
        Assert.IsEmpty(await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public void HistoryCommitRecoveryRequiredExceptionPreservesCommittedPackIdAndMessage()
    {
        var packId = PackId.New();
        var inner = new InvalidOperationException("disk write error");
        var ex = new HistoryCommitRecoveryRequiredException(packId, "local state recovery required", inner);

        Assert.AreEqual(packId, ex.CommittedPackId);
        Assert.AreEqual("local state recovery required", ex.Message);
        Assert.AreSame(inner, ex.InnerException);
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
