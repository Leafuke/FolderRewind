using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SourceHistoryTests
{
    private string _root = null!;
    private readonly HistoryConfigId _config = new(Guid.NewGuid().ToString());

    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindSourceHistory", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task IndependentSourcesEstablishSameNamedBranchesAndCheckoutOnlyTheirOwnFiles()
    {
        await using var history = await Open("isolation");
        var a = SourceId.New(); var b = SourceId.New();
        var first = await Capture(history, [a, b], (a, "a1"));
        Assert.HasCount(1, first.Run.SourceResults);
        Assert.HasCount(1, first.NewBranchUpdates);
        var firstState = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(a);
        var second = await Capture(history, [a, b], (b, "b1"));
        var state = (await history.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(firstState, state.GetSourceState(a));
        Assert.AreEqual("main", first.NewBranchUpdates.Single().Name);
        Assert.AreEqual("main", second.NewBranchUpdates.Single().Name);
        Assert.AreNotEqual(state.GetSourceState(a).ActiveBranchId, state.GetSourceState(b).ActiveBranchId);
        var feature = await history.Branches.CreateFromCheckpointAsync(first.NewCheckpoints.Single().CheckpointId, "feature", sourceId: a);
        var restore = Restore(history);
        var quick = await new HistoryQuickRestoreResolver(history, restore).ResolveAsync(b, AssessmentDepth.Deep);
        Assert.AreEqual(second.NewVersions.Single().VersionId, quick.VersionId);
        var targetA = Target("a", "working-a"); var targetB = Target("b", "working-b");
        var result = await new HistoryCheckoutService(history, restore).CheckoutAsync(feature.BranchUpdate.UpdateId,
            [new(a, targetA), new(b, targetB)], state, HistoryCheckoutProtectionMode.DiscardCurrentChanges);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual("a1", File.ReadAllText(Path.Combine(targetA, "state.txt")));
        Assert.AreEqual("working-b", File.ReadAllText(Path.Combine(targetB, "state.txt")));
        Assert.AreEqual(state.GetSourceState(b), (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(b));
    }

    [TestMethod]
    public async Task RestoreKeepsContinuationSeparateFromContentParentsAndMergeIsSourceScoped()
    {
        await using var history = await Open("ancestry");
        var a = SourceId.New(); var b = SourceId.New();
        var initial = await Capture(history, [a, b], (a, "a1"), (b, "b1"));
        var a1 = initial.NewCheckpoints.Single(c => c.SourceId == a);
        var feature = await history.Branches.CreateFromCheckpointAsync(a1.CheckpointId, "feature", sourceId: a);
        var next = await Capture(history, [a, b], (a, "a2"));
        var a2 = next.NewCheckpoints.Single();
        var restore = Restore(history);
        var before = (await history.WorkspaceStore.LoadAsync()).Value!;
        var target = Target("ancestry-target", "a2");
        var restored = await restore.RestoreVersionAsync(a1.VersionId, new(a, target), before, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(restored.Succeeded, restored.Diagnostic);
        var afterRestore = (await history.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(before.GetSourceState(a).ActiveBranchUpdateId, afterRestore.GetSourceState(a).ActiveBranchUpdateId);
        Assert.AreEqual(a2.CheckpointId, afterRestore.GetSourceState(a).CheckpointAncestryAnchorId);
        var third = await Capture(history, [a, b], (a, "a3"));
        Assert.AreEqual(a1.VersionId, third.NewVersions.Single().ParentVersionIds.Single());
        Assert.AreEqual(a2.CheckpointId, third.NewCheckpoints.Single().ParentCheckpointIds.Single());
        var plan = await new HistoryMergePlanner(history).BuildAsync(feature.BranchUpdate.BranchId,
            (await history.WorkspaceStore.LoadAsync()).Value!, "test", [new(a, target), new(b, Target("other", "b"))], sourceId: a);
        Assert.AreEqual(HistoryMergeMode.NoOp, plan.Mode);
        Assert.HasCount(1, plan.Bindings);
        var bBranch = initial.NewBranchUpdates.Single(u => u.SourceId == b);
        var current = (await history.WorkspaceStore.LoadAsync()).Value!;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new HistoryMergePlanner(history).BuildAsync(bBranch.BranchId,
            current, "test", [], sourceId: a));

        var main = third.NewBranchUpdates.Single();
        var checkout = new HistoryCheckoutService(history, restore);
        Assert.IsTrue((await checkout.CheckoutAsync(feature.BranchUpdate.UpdateId, [new(a, target)], current,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges)).Succeeded);
        var featureCapture = await Capture(history, [a, b], (a, "feature-a"));
        var featureTip = featureCapture.NewBranchUpdates.Single();
        Assert.IsTrue((await checkout.CheckoutAsync(main.UpdateId, [new(a, target)],
            (await history.WorkspaceStore.LoadAsync()).Value!, HistoryCheckoutProtectionMode.DiscardCurrentChanges)).Succeeded);
        var service = new HistoryMergeService(history, restore);
        var session = (await service.StartAsync(featureTip.BranchId, "test", [new(a, target)]))!;
        Assert.AreEqual(HistoryMergeMode.ThreeWay, session.Plan.Mode);
        Assert.AreEqual(a1.CheckpointId, session.Plan.BaseCheckpointId);
        foreach (var conflict in service.AllConflicts(session).Select(pair => pair.Conflict).ToArray())
            session = history.MergeSessions.Resolve(session, new(session.Plan.Revision, conflict.Id,
                conflict.InputSignature, MergeResolutionChoice.Ours));
        var untouched = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(b);
        var unusedArchives = new UnusedArchiveBackend();
        var merged = await new HistoryMergeApplyService(history, restore,
            new HistoryMergeCommitBuilder(history, restore, unusedArchives, unusedArchives)).ApplyAsync(session);
        Assert.IsTrue(merged.Succeeded, merged.Diagnostic);
        var mergeState = (await history.WorkspaceStore.LoadAsync()).Value!;
        var mergeCheckpoint = await history.Query.GetCheckpointAsync(mergeState.GetSourceState(a).CheckpointAncestryAnchorId!.Value);
        Assert.AreEqual(CheckpointCreationKind.Merge, mergeCheckpoint!.CreationKind);
        Assert.AreEqual(a, mergeCheckpoint.SourceId);
        Assert.AreEqual(untouched, mergeState.GetSourceState(b));
        Assert.AreEqual(featureTip.UpdateId, (await history.Query.GetBranchTipsAsync(featureTip.BranchId)).Single().UpdateId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchRestoreIsAtomicAndPreservesIndependentBranchPointers(bool failSecondApply)
    {
        await using var history = await Open("batch");
        var a = SourceId.New(); var b = SourceId.New();
        var captured = await Capture(history, [a, b], (a, "a1"), (b, "b1"));
        var before = (await history.WorkspaceStore.LoadAsync()).Value!;
        var targetA = Target("batch-a", "working-a"); var targetB = Target("batch-b", "working-b");
        IHistoryRestoreMutationBackend backend = new FileSystemHistoryRestoreMutationBackend();
        if (failSecondApply) backend = new FailSecondApply(backend);
        var result = await Restore(history, backend).RestoreVersionsAsync(captured.NewVersions.ToDictionary(v => v.SourceId, v => v.VersionId),
            [new(a, targetA), new(b, targetB)], before, HistoryRestoreApplyMode.Clean);
        Assert.AreEqual(failSecondApply ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
        Assert.AreEqual(failSecondApply ? "working-a" : "a1", File.ReadAllText(Path.Combine(targetA, "state.txt")));
        Assert.AreEqual(failSecondApply ? "working-b" : "b1", File.ReadAllText(Path.Combine(targetB, "state.txt")));
        var after = (await history.WorkspaceStore.LoadAsync()).Value!;
        foreach (var id in new[] { a, b })
        {
            Assert.AreEqual(before.GetSourceState(id).ActiveBranchId, after.GetSourceState(id).ActiveBranchId);
            Assert.AreEqual(before.GetSourceState(id).ActiveBranchUpdateId, after.GetSourceState(id).ActiveBranchUpdateId);
        }
        if (failSecondApply) Assert.IsTrue(HistoryWorkspace.StateEquals(before, after));
    }

    [TestMethod]
    public async Task PackUnionAndIndexRebuildPreserveSourceOwnershipAndLocalWorkspace()
    {
        await using var first = await Open("first-device"); await using var second = await Open("second-device");
        var a = SourceId.New(); var b = SourceId.New();
        await Capture(first, [a, b], (a, "a1")); await Capture(second, [a, b], (b, "b1"));
        var before = (await second.WorkspaceStore.LoadAsync()).Value!;
        await second.Repository.ImportAsync((await first.Repository.ReadAllPacksAsync()).Select(p => (ReadOnlyMemory<byte>)p.OriginalBytes).ToArray());
        await second.Index.RebuildAsync(await second.Repository.ReadAllPacksAsync());
        var snapshot = await new HistoryPresentationQueryService(second).QueryAsync();
        Assert.HasCount(2, snapshot.Branches);
        Assert.IsFalse(snapshot.Branches.Any(branch => branch.HasNameCollision));
        Assert.IsTrue(HistoryWorkspace.StateEquals(before, (await second.WorkspaceStore.LoadAsync()).Value!));
        Assert.HasCount(1, (await new HistoryPresentationQueryService(second).QueryAsync(a)).Branches);
        Assert.HasCount(1, (await new HistoryPresentationQueryService(second).QueryAsync(b)).Branches);
    }

    private async Task<HistoryRuntime> Open(string name)
    {
        var history = new HistoryRuntime(new FileHistoryRepository(_config, new HistoryRepositoryPaths(Path.Combine(_root, name))));
        await history.InitializeAsync(); return history;
    }

    private async Task<HistoryCommitBatch> Capture(HistoryRuntime history, SourceId[] roster, params (SourceId Source, string Content)[] items)
    {
        var workspace = (await history.WorkspaceStore.LoadAsync()).Value;
        var captures = items.Select(item =>
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin"); File.WriteAllText(path, item.Content);
            var bytes = File.ReadAllBytes(path); var representation = RepresentationId.New();
            return new SourceCaptureResult(item.Source, SourceCaptureOutcome.Captured, CaptureScope.FullSource, item.Content, null,
                new RepresentationCandidate(representation, RepresentationKind.CoreFull, "source-test", [], MaterializationFidelity.Exact,
                    null, item.Content, new Dictionary<string, string> { ["content"] = item.Content }),
                new LocalReplicaCandidate(LocalReplicaId.New(), representation, LocalReplicaLocator.ControlledAbsolute(path), CapturePayloadState.VerifiedFinal, DateTimeOffset.UtcNow),
                new CapturePayloadCandidate(path, CapturePayloadState.VerifiedFinal, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))),
                workspace?.StateRevision ?? -1, workspace?.GetSourceState(item.Source).BaseVersionId, cleanupHandle: null, diagnostics: []);
        });
        var now = DateTimeOffset.UtcNow;
        return await history.Commit.CommitAsync(new(new HistoryConfigSnapshot(_config,
            roster.Select(id => new HistoryConfigSourceSnapshot(id, new(id.ToString(), Path.Combine(_root, id.ToString()))))),
            new(RunId.New(), now, now, BackupInvocationKind.Manual, HistoryProvenance.Native("test")), workspace, captures));
    }

    private string Target(string name, string content) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "state.txt"), content); return path; }
    private static HistoryRestoreService Restore(HistoryRuntime history, IHistoryRestoreMutationBackend? backend = null)
        => new(history, new RepresentationRuntime([new TextRepresentation()]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])), backend ?? new FileSystemHistoryRestoreMutationBackend());

    private sealed class TextRepresentation : IRepresentationHandler
    {
        public bool CanHandle(VersionRepresentation representation) => representation.Format == "source-test";
        public ValueTask<RepresentationAssessment> AssessAsync(RepresentationAssessmentContext context, CancellationToken token)
            => ValueTask.FromResult(new RepresentationAssessment(context.Representation.RepresentationId, HistoryReadiness.Ready, MaterializationFidelity.Exact, [], []));
        public ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
        { Directory.CreateDirectory(context.StagingDirectory); File.WriteAllText(Path.Combine(context.StagingDirectory, "state.txt"), context.Representation.RepresentationSpecificMetadata["content"]); return ValueTask.CompletedTask; }
    }

    private sealed class FailSecondApply(IHistoryRestoreMutationBackend inner) : IHistoryRestoreMutationBackend
    {
        private int _count;
        public HistoryRestoreRollbackSnapshot PlanRollback(HistoryRestoreSourceBinding source, HistoryTransactionId id) => inner.PlanRollback(source, id);
        public Task PrepareRollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => inner.PrepareRollbackAsync(snapshot, token);
        public Task ApplyAsync(HistoryRestoreSourceBinding source, string staging, HistoryRestoreApplyMode mode, HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
            => ++_count == 2 ? Task.FromException(new IOException("Injected second-source failure")) : inner.ApplyAsync(source, staging, mode, snapshot, token);
        public Task RollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => inner.RollbackAsync(snapshot, token);
        public Task CommitAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => inner.CommitAsync(snapshot, token);
    }

    private sealed class UnusedArchiveBackend : IHistoryCompactionBackend, IArchiveRepresentationBackend
    {
        public Task<HistoryCompactionPayload> CreateFullAsync(SourceVersion version, string materializedDirectory,
            RepresentationId replacementRepresentationId, string durableOutputDirectory, CancellationToken token)
            => throw new AssertFailedException("Whole-side resolution should reuse an existing Exact Version.");
        public ValueTask<PayloadVerificationResult> DeepVerifyAsync(VersionRepresentation representation, string payloadPath, CancellationToken token)
            => throw new AssertFailedException("No new archive should be created.");
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string localPath, CancellationToken token)
            => throw new AssertFailedException("No new archive should be created.");
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
            => throw new AssertFailedException("Existing Versions use their registered materializer.");
    }
}
