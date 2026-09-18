using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using System.Collections.Immutable;
using System.IO.Compression;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeApplyTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindMerge", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    [DataRow("success")]
    [DataRow("second-source")]
    [DataRow("dirty")]
    [DataRow("config")]
    [DataRow("stale-session")]
    public async Task ThreeWayApplyIsAtomicAndLeavesSourceTipUnchanged(string failure)
    {
        var config = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var history = new HistoryRuntime(new FileHistoryRepository(config, new HistoryRepositoryPaths(Path.Combine(_root, "repo"))));
        await history.InitializeAsync();
        var handler = new TreeHandler(); var facts = new List<object>();
        var sourceIds = new[] { SourceId.New(), SourceId.New() };
        SourceVersion Version(SourceId source, string name, SourceVersion? parent, params (string Path, string Text)[] files)
        {
            var version = new SourceVersion(VersionId.New(), config, source, parent is null ? [] : [parent.VersionId], DateTimeOffset.UtcNow,
                null, CaptureScope.FullSource, CaptureOutcome.Captured, [], new SourceDescriptorSnapshot(name, name), null, HistoryProvenance.Native("test"));
            var rep = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "tree", [], MaterializationFidelity.Exact, null, null, null);
            handler.Trees[rep.RepresentationId] = files.ToDictionary(f => f.Path, f => f.Text);
            facts.Add(version); facts.Add(rep); return version;
        }
        var b = sourceIds.Select(id => Version(id, "base", null, ("a.txt", "base-a"), ("b.txt", "base-b"))).ToArray();
        var o = b.Select(v => Version(v.SourceId, "ours", v, ("a.txt", "ours-a"), ("b.txt", "base-b"))).ToArray();
        var t = b.Select(v => Version(v.SourceId, "theirs", v, ("a.txt", "base-a"), ("b.txt", "theirs-b"))).ToArray();
        ConfigurationCheckpoint Checkpoint(SourceVersion[] versions, ConfigurationCheckpoint? parent)
        {
            var cp = new ConfigurationCheckpoint(CheckpointId.New(), config, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
                versions.Select(v => new CheckpointSource(v.SourceId, v.SourceDescriptorSnapshot, v.VersionId, CheckpointSourceDisposition.Captured, v.EffectiveSourceBoundary)),
                parent is null ? [] : [parent.CheckpointId]);
            facts.Add(cp); return cp;
        }
        var bc = Checkpoint(b, null); var oc = Checkpoint(o, bc); var tc = Checkpoint(t, bc);
        var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", oc.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", tc.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        facts.AddRange([ours, theirs]); var codec = new HistoryPackCodec();
        await history.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, facts.Select(f => codec.CreateObject(f))));
        await history.EnsureIndexCurrentAsync();
        var workspace = new HistoryWorkspace(config, 0, ours.BranchId, ours.UpdateId,
            o.Select(v => new WorkspaceSourceBaseline(v.SourceId, v.VersionId, WorkspaceBaselineRelation.Exact)), oc.CheckpointId);
        await history.WorkspaceStore.SaveAsync(workspace, -1);
        await history.LocalReplicaCatalogStore.SaveAsync(new(config, 0, []), -1);
        var bindings = sourceIds.Select(id => new HistoryRestoreSourceBinding(id, Path.Combine(_root, id.ToString()), EffectiveSourceBoundarySnapshot.All)).ToArray();
        foreach (var binding in bindings)
        {
            Directory.CreateDirectory(binding.TargetDirectory);
            File.WriteAllText(Path.Combine(binding.TargetDirectory, "a.txt"), "ours-a");
            File.WriteAllText(Path.Combine(binding.TargetDirectory, "b.txt"), "base-b");
        }
        var backend = new FailingBackend(failure == "second-source");
        var restore = new HistoryRestoreService(history, new RepresentationRuntime([handler]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])), backend);
        var session = (await new HistoryMergeService(history, restore).StartAsync(theirs.BranchId, "config-1", bindings))!;
        Assert.AreEqual(MergeSessionState.Ready, session.State);
        Assert.HasCount(6, history.MergeSessions.ActiveRoots());
        if (failure == "dirty") File.WriteAllText(Path.Combine(bindings[0].TargetDirectory, "a.txt"), "dirty!");
        if (failure == "stale-session") history.MergeSessions.Update(session, MergeSessionState.Abandoned);
        var archive = new ZipBackend();
        var result = await new HistoryMergeApplyService(history, restore, new(history, restore, archive, archive),
            reload: _ => Task.FromResult((failure == "config" ? "config-2" : "config-1", (IReadOnlyList<HistoryRestoreSourceBinding>)bindings))).ApplyAsync(session);
        var current = (await history.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(theirs.UpdateId, HistoryBranchProjection.Build(await history.Query.GetAllBranchUpdatesAsync()).Single(p => p.BranchId == theirs.BranchId).Tips.Single().UpdateId);
        if (failure == "success")
        {
            Assert.AreEqual(HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreNotEqual(workspace.ActiveBranchUpdateId, current.ActiveBranchUpdateId);
            var merged = (await history.Query.GetAllBranchUpdatesAsync()).Single(u => u.Reason == BranchUpdateReason.Merged);
            Assert.AreEqual(bc.CheckpointId, merged.MergeProvenance!.BaseCheckpointId);
            Assert.HasCount(2, (await history.Query.GetCheckpointAsync(merged.TargetCheckpointId!.Value))!.ParentCheckpointIds);
            Assert.HasCount(2, (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
            Assert.HasCount(0, history.MergeSessions.ActiveRoots());
            foreach (var binding in bindings)
            {
                Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(binding.TargetDirectory, "a.txt")));
                Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
            }
            var retry = await new HistoryMergeApplyService(history, restore, new(history, restore, archive, archive)).ApplyAsync(session);
            Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, retry.Status);
        }
        else
        {
            Assert.AreEqual(failure == "second-source" ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.BlockedBeforeMutation, result.Status, result.Diagnostic);
            if (failure == "config") Assert.AreEqual(MergeSessionState.Stale, history.MergeSessions.Load(session.Id).State);
            Assert.IsTrue(HistoryRestoreTransactionJournalStore.WorkspaceEquals(workspace, current));
            Assert.HasCount(0, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged).ToArray());
            foreach (var binding in bindings) Assert.AreEqual("base-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
        }
    }

    private sealed class TreeHandler : IRepresentationHandler
    {
        public Dictionary<RepresentationId, Dictionary<string, string>> Trees { get; } = [];
        public bool CanHandle(VersionRepresentation representation) => representation.Format == "tree";
        public ValueTask<RepresentationAssessment> AssessAsync(RepresentationAssessmentContext context, CancellationToken token)
            => ValueTask.FromResult(new RepresentationAssessment(context.Representation.RepresentationId, HistoryReadiness.Ready, MaterializationFidelity.Exact, [], []));
        public ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
        {
            Directory.CreateDirectory(context.StagingDirectory);
            foreach (var pair in Trees[context.Representation.RepresentationId]) File.WriteAllText(Path.Combine(context.StagingDirectory, pair.Key), pair.Value);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ZipBackend : IHistoryCompactionBackend, IArchiveRepresentationBackend
    {
        public Task<HistoryCompactionPayload> CreateFullAsync(SourceVersion version, string source, RepresentationId id, string output, CancellationToken token)
        {
            Directory.CreateDirectory(output); var path = Path.Combine(output, "full.zip"); ZipFile.CreateFromDirectory(source, path);
            return Task.FromResult(new HistoryCompactionPayload("zip", path, new FileInfo(path).Length, null, null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<PayloadVerificationResult> DeepVerifyAsync(VersionRepresentation representation, string path, CancellationToken token) => VerifyAsync(representation, path, token);
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
        { using var zip = ZipFile.OpenRead(path); foreach (var entry in zip.Entries) { using var s = entry.Open(); s.CopyTo(Stream.Null); } return ValueTask.FromResult(new PayloadVerificationResult(true, "zip", "")); }
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
        { foreach (var input in inputs) ZipFile.ExtractToDirectory(input.LocalPath, staging); return ValueTask.CompletedTask; }
    }
    private sealed class FailingBackend(bool fail) : IHistoryRestoreMutationBackend
    {
        private readonly FileSystemHistoryRestoreMutationBackend _inner = new(); private int _count;
        public HistoryRestoreRollbackSnapshot PlanRollback(HistoryRestoreSourceBinding source, HistoryTransactionId transaction) => _inner.PlanRollback(source, transaction);
        public Task PrepareRollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => _inner.PrepareRollbackAsync(snapshot, token);
        public Task ApplyAsync(HistoryRestoreSourceBinding source, string staging, HistoryRestoreApplyMode mode, HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
        { if (fail && ++_count == 2) throw new IOException("Injected second Source failure."); return _inner.ApplyAsync(source, staging, mode, snapshot, token); }
        public Task RollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => _inner.RollbackAsync(snapshot, token);
        public Task CommitAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => _inner.CommitAsync(snapshot, token);
    }
}
