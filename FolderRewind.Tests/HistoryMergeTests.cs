using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeTests
{
    private static readonly HistoryConfigId Config = new("merge-tests");
    private static ConfigurationCheckpoint Checkpoint(params CheckpointId[] parents) => new(CheckpointId.New(), Config,
        DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"), [], parents,
        parents.Length == 2 ? CheckpointCreationKind.Merge : CheckpointCreationKind.Capture);

    [TestMethod]
    public void GraphDistinguishesBestBaseFromControlHistoryAndRejectsBrokenParents()
    {
        var b = Checkpoint(); var o = Checkpoint(b.CheckpointId); var t = Checkpoint(b.CheckpointId);
        var left = Checkpoint(o.CheckpointId, t.CheckpointId); var right = Checkpoint(t.CheckpointId, o.CheckpointId);
        var graph = new HistoryCheckpointGraph([b, o, t, left, right], Config);
        Assert.AreEqual(new HistoryMergeBase(HistoryMergeMode.ThreeWay, b.CheckpointId), graph.FindBase(o.CheckpointId, t.CheckpointId));
        Assert.AreEqual(HistoryMergeMode.NoOp, graph.FindBase(left.CheckpointId, o.CheckpointId).Mode);
        Assert.AreEqual(HistoryMergeMode.FastForwardLike, graph.FindBase(o.CheckpointId, left.CheckpointId).Mode);
        Assert.AreEqual(HistoryMergeMode.MultipleMergeBases, graph.FindBase(left.CheckpointId, right.CheckpointId).Mode);
        var unrelated = Checkpoint();
        Assert.AreEqual(HistoryMergeMode.NoCommonBase, new HistoryCheckpointGraph([o, b, unrelated], Config).FindBase(o.CheckpointId, unrelated.CheckpointId).Mode);
        Assert.ThrowsExactly<InvalidOperationException>(() => new HistoryCheckpointGraph([o], Config));
    }

    [TestMethod]
    [DataRow(null, "o", null, "o", false)]
    [DataRow("b", null, "b", null, false)]
    [DataRow("b", "o", "b", "o", false)]
    [DataRow("b", "b", "t", "t", false)]
    [DataRow(null, "same", "same", "same", false)]
    [DataRow("b", null, "t", null, true)]
    [DataRow(null, "", "t", null, true)]
    [DataRow("b", "o", "t", null, true)]
    public void FileThreeWayRulesPreserveAbsentAndEmpty(string? b, string? o, string? t, string? expected, bool conflict)
    {
        MergeTreeManifest Tree(string? value) => value is null ? MergeTreeManifest.Empty
            : MergeTreeManifest.Create(new Dictionary<string, MergeFileValue> { ["file"] = new("controlled", value, value.Length) });
        var result = new GenericFileMergeProvider().Analyze(SourceId.New(), Tree(b), Tree(o), Tree(t));
        Assert.HasCount(conflict ? 1 : 0, result.Conflicts);
        if (!conflict) Assert.AreEqual(expected, result.Automatic.Files.GetValueOrDefault("file")?.Digest);
    }

    [TestMethod]
    public void StructuralConflictIsAtomicAndAllowsUnchangedOppositeSide()
    {
        MergeTreeManifest Tree(string path, string digest) => MergeTreeManifest.Create(new Dictionary<string, MergeFileValue> { [path] = new("controlled", digest, 1) });
        var provider = new GenericFileMergeProvider(); var source = SourceId.New();
        var changed = provider.Analyze(source, Tree("a", "b"), Tree("a/file", "o"), Tree("a", "t"));
        Assert.AreEqual(MergeConflictKind.PathStructure, changed.Conflicts.Single().Kind);
        Assert.HasCount(2, changed.Conflicts.Single().Subject.Paths);
        var unilateral = provider.Analyze(source, Tree("a", "b"), Tree("a/file", "o"), Tree("a", "b"));
        Assert.IsEmpty(unilateral.Conflicts); Assert.IsTrue(unilateral.Automatic.Files.ContainsKey("a/file"));
    }

    [TestMethod]
    public void DirectoryCaseCollisionIsReportedAsOneStructuralConflict()
    {
        MergeTreeManifest Tree(string path, string digest) => MergeTreeManifest.Create(
            new Dictionary<string, MergeFileValue> { [path] = new("controlled", digest, 1) });
        var result = new GenericFileMergeProvider().Analyze(SourceId.New(), MergeTreeManifest.Empty,
            Tree("World/ours.dat", "o"), Tree("world/theirs.dat", "t"));

        var conflict = result.Conflicts.Single();
        Assert.AreEqual(MergeConflictKind.PathStructure, conflict.Kind);
        CollectionAssert.AreEquivalent(new[] { "World/ours.dat", "world/theirs.dat" }, conflict.Subject.Paths.ToArray());
    }

    [TestMethod]
    [DataRow(null, null, "T", HistoryMergeSourceAction.Reuse)]
    [DataRow(null, "O", null, HistoryMergeSourceAction.Reuse)]
    [DataRow(null, "A", "A", HistoryMergeSourceAction.Reuse)]
    [DataRow(null, "O", "T", HistoryMergeSourceAction.SourceAddAdd)]
    [DataRow("A", null, null, HistoryMergeSourceAction.Remove)]
    [DataRow("A", null, "A", HistoryMergeSourceAction.Remove)]
    [DataRow("A", "A", null, HistoryMergeSourceAction.Remove)]
    [DataRow("A", null, "T", HistoryMergeSourceAction.SourceDeleteModify)]
    [DataRow("A", "O", null, HistoryMergeSourceAction.SourceModifyDelete)]
    [DataRow("A", "O", "T", HistoryMergeSourceAction.MergeFiles)]
    public void SourceRosterRulesKeepAbsenceSeparateFromUnknown(string? b, string? o, string? t, HistoryMergeSourceAction action)
    {
        var id = SourceId.New(); var versions = new Dictionary<string, VersionId>();
        CheckpointSource? Source(string? name)
        {
            if (name is null) return null;
            if (!versions.TryGetValue(name, out var version)) versions.Add(name, version = VersionId.New());
            return new(id, new SourceDescriptorSnapshot(name, name), version, CheckpointSourceDisposition.Captured, EffectiveSourceBoundarySnapshot.All);
        }
        Assert.AreEqual(action, HistoryMergePlanner.PlanSource(id, Source(b), Source(o), Source(t)).Action);
        var unknown = new CheckpointSource(id, new SourceDescriptorSnapshot("unknown", "unknown"), null, CheckpointSourceDisposition.Unavailable);
        Assert.ThrowsExactly<InvalidOperationException>(() => HistoryMergePlanner.PlanSource(id, null, unknown, null));
    }

    [TestMethod]
    public void FrozenConfigCollectionsRejectAllWritersAndResumeAfterLease()
    {
        var values = new FolderRewind.Services.GuardedDictionary<string>(); values.Add("key", "original");
        var list = new FolderRewind.Services.GuardedObservableCollection<string>(["original"]);
        using (FolderRewind.Services.ConfigMutationProtection.Freeze([values, list]))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => values["key"] = "changed");
            Assert.ThrowsExactly<InvalidOperationException>(() => values.Clear());
            Assert.ThrowsExactly<InvalidOperationException>(() => list.RemoveAt(0));
            Assert.ThrowsExactly<InvalidOperationException>(() => list[0] = "changed");
        }
        values["key"] = "changed"; list.Clear();
        Assert.AreEqual("changed", values["key"]); Assert.IsEmpty(list);
    }

    [TestMethod]
    public void DurableSessionRoundTripsResolutionAndKeepsRootsUntilAbandoned()
    {
        var root = Path.Combine(Path.GetTempPath(), "merge-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = SourceId.New(); var version = VersionId.New(); var cp = Checkpoint();
            var o = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", cp.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
            var t = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", cp.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
            var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, o, t, cp.CheckpointId,
                new HistoryWorkspace(Config, 0, o.BranchId, o.UpdateId, []), "revision", [], []);
            var store = new MergeSessionStore(root); var session = store.Create(plan, [version]);
            MergeTreeManifest Tree(string digest) => MergeTreeManifest.Create(new Dictionary<string, MergeFileValue> { ["file"] = new("controlled", digest, 1) });
            var proposal = new GenericFileMergeProvider().Analyze(source, Tree("b"), Tree("o"), Tree("t"));
            var conflict = proposal.Conflicts.Single();
            store.SaveSource(session, new(new(source, null, null, null, HistoryMergeSourceAction.MergeFiles, null), proposal.Automatic, Tree("b"), Tree("o"), Tree("t")), proposal.Conflicts);
            session = store.Update(session, MergeSessionState.Resolving);
            var old = session;
            session = store.Resolve(session, new(plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours));
            store = new MergeSessionStore(root);
            Assert.AreEqual(MergeSessionState.Ready, store.Load(session.Id).State);
            Assert.AreEqual(MergeResolutionChoice.Ours, store.Conflicts(session).Single().Resolution!.Choice);
            Assert.Contains(version, store.ActiveRoots());
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Update(old, MergeSessionState.Abandoned));
            store.Update(session, MergeSessionState.Abandoned);
            Assert.IsEmpty(store.ActiveRoots());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
