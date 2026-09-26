using System.Collections.Immutable;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CachePostActionFailurePreservesCommittedFacts(bool invalidationFails)
    {
        var source = SourceId.New(); var invalidated = false;
        var failures = new List<(string Stage, Exception Error)>();
        var original = new HistoryRestoreResult(HistoryRestoreStatus.Committed, "", true, [source]);
        var result = await MergePostActions.CompleteAsync(original, token =>
        {
            Assert.IsFalse(token.CanBeCanceled); throw new OperationCanceledException("late cancellation");
        }, token =>
        {
            Assert.IsFalse(token.CanBeCanceled); invalidated = true;
            if (invalidationFails) throw new IOException("cache inaccessible");
            return Task.CompletedTask;
        }, (stage, error) => failures.Add((stage, error)));
        Assert.HasCount(invalidationFails ? 2 : 1, failures);
        Assert.AreEqual("capture-cache-synchronize", failures[0].Stage);
        Assert.IsInstanceOfType<OperationCanceledException>(failures[0].Error);
        Assert.IsNotNull(failures[0].Error.StackTrace);
        if (invalidationFails) Assert.AreEqual("capture-cache-invalidate", failures[1].Stage);
        Assert.IsTrue(invalidated); Assert.IsTrue(result.TargetCommitted); Assert.IsTrue(result.WorkspaceUpdated);
        CollectionAssert.AreEqual(original.AppliedSources.ToArray(), result.AppliedSources.ToArray());
        Assert.AreEqual(HistoryRestoreStatus.CommittedWithPostActionWarning, result.Status);
        Assert.AreEqual(MergeDiagnosticCode.PostActionWarning, result.MergeDiagnostic!.Code);
    }

    [TestMethod]
    public void SessionActionsAndDiagnosticsHaveLocalizedStateContracts()
    {
        foreach (var state in Enum.GetValues<MergeSessionState>())
        {
            Assert.AreEqual(state == MergeSessionState.Ready, MergeSessionActions.Allowed("Merge_Apply", state, true));
            Assert.AreEqual(state is MergeSessionState.Resolving or MergeSessionState.Ready, MergeSessionActions.Allowed("Merge_Manual", state, true));
            if (state is MergeSessionState.Committed or MergeSessionState.Abandoned)
                foreach (var key in new[] { "Merge_Apply", "Merge_Abandon", "Merge_Recompute", "Merge_Resume", "Merge_PreviewBase" })
                    Assert.IsFalse(MergeSessionActions.Allowed(key, state, true));
        }
        Assert.IsFalse(MergeSessionActions.Allowed("Merge_New", null, false));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "FolderRewind", "Strings"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            var data = System.Xml.Linq.XDocument.Load(Path.Combine(directory.FullName, "FolderRewind", "Strings", locale, "Resources.resw"))
                .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
            foreach (var code in Enum.GetValues<MergeDiagnosticCode>())
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data["Merge_Diagnostic_" + code]));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data[new HistoryMergeDiagnostic(code).NextActionKey]));
            }
            foreach (var kind in Enum.GetValues<SourceVersionCreationKind>()) Assert.IsTrue(data.ContainsKey("History_Creation_" + kind));
        }
    }

    [TestMethod]
    public void ProviderCannotEscapeOrDoubleClaimInputPaths()
    {
        var id = SourceId.New();
        var tree = MergeTreeManifest.Create(new Dictionary<string, MergeFileValue> { ["file"] = new("controlled", "digest", 1) });
        var valid = new GenericFileMergeProvider().Analyze(id, MergeTreeManifest.Empty, tree, tree);
        HistoryMergeService.ValidateProposal(id, MergeTreeManifest.Empty, tree, tree, valid);
        Assert.ThrowsExactly<InvalidDataException>(() => HistoryMergeService.ValidateProposal(id, MergeTreeManifest.Empty, tree, tree,
            valid with { HandledPaths = ["file", "file"] }));
        Assert.ThrowsExactly<InvalidDataException>(() => HistoryMergeService.ValidateProposal(id, MergeTreeManifest.Empty, tree, tree,
            valid with { Automatic = MergeTreeManifest.Create(new Dictionary<string, MergeFileValue> { ["../escape"] = new("external", "digest", 1) }) }));
    }
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
    [DataRow("ours")]
    [DataRow("theirs")]
    [DataRow("manual")]
    [DataRow("manual-corrupt")]
    [DataRow("changed")]
    public async Task DurableSessionRoundTripsResolutionAndKeepsRootsUntilAbandoned(string scenario)
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
            var sourcePlan = new HistoryMergeSourcePlan(source, null, null, null, HistoryMergeSourceAction.MergeFiles, null);
            var signature = HistoryMergeService.ConflictSignature(plan, sourcePlan, conflict);
            var relocated = conflict with { Ours = conflict.Ours.ToImmutableSortedDictionary(p => p.Key,
                p => p.Value with { Handle = "different/local/path" }, StringComparer.Ordinal) };
            Assert.AreEqual(signature, HistoryMergeService.ConflictSignature(plan with { Revision = Guid.NewGuid(), ConfigRevision = "fixed-binding" }, sourcePlan, relocated));
            Assert.AreNotEqual(signature, HistoryMergeService.ConflictSignature(plan with { ProviderVersion = "other@2;schema=2" }, sourcePlan, conflict));
            Assert.AreNotEqual(signature, HistoryMergeService.ConflictSignature(plan with { PolicyVersion = "other@2" }, sourcePlan, conflict));
            Assert.AreNotEqual(signature, HistoryMergeService.ConflictSignature(plan, sourcePlan, conflict with { Ours = conflict.Theirs }));
            store.SaveSource(session, new(new(source, null, null, null, HistoryMergeSourceAction.MergeFiles, null), proposal.Automatic, Tree("b"), Tree("o"), Tree("t")), proposal.Conflicts);
            session = store.Update(session, MergeSessionState.Resolving);
            var old = session;
            MergeFileValue? manual = null;
            if (scenario.StartsWith("manual", StringComparison.Ordinal))
            {
                var owned = Path.Combine(store.SessionDirectory(session.Id), "manual"); Directory.CreateDirectory(owned);
                File.WriteAllText(Path.Combine(owned, "content"), "manual");
                manual = (await MergeTreeManifest.ReadAsync(owned, _ => true, default)).Files["content"];
            }
            var choice = manual is not null ? MergeResolutionChoice.Manual : scenario == "theirs" ? MergeResolutionChoice.Theirs : MergeResolutionChoice.Ours;
            session = store.Resolve(session, new(plan.Revision, conflict.Id, conflict.InputSignature, choice, manual));
            store = new MergeSessionStore(root);
            Assert.AreEqual(MergeSessionState.Ready, store.Load(session.Id).State);
            Assert.AreEqual(choice, store.Conflicts(session).Single().Resolution!.Choice);
            Assert.Contains(version, store.ActiveRoots());
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Update(old, MergeSessionState.Abandoned));
            if (scenario == "manual-corrupt") File.WriteAllText(manual!.Handle, "corrupted");
            var nextRoot = VersionId.New();
            session = store.Replan(session, plan with { Revision = Guid.NewGuid() }, [nextRoot]);
            Assert.Contains(version, store.ActiveRoots()); Assert.Contains(nextRoot, store.ActiveRoots());
            Assert.AreEqual(plan.Revision, old.Plan.Revision);
            Assert.AreEqual(MergeSessionState.Preparing, store.Load(session.Id).State);
            var nextConflict = conflict with { Id = "new-conflict", InputSignature = scenario == "changed" ? "changed" : conflict.InputSignature };
            store.SaveSource(session, new(new(source, null, null, null, HistoryMergeSourceAction.MergeFiles, null), proposal.Automatic, Tree("b"), Tree("o"), Tree("t")), [nextConflict]);
            session = store.CompletePreparation(session, [nextRoot]);
            Assert.DoesNotContain(version, store.ActiveRoots()); Assert.Contains(nextRoot, store.ActiveRoots());
            var resolution = store.Conflicts(session).Single().Resolution;
            if (scenario is "changed" or "manual-corrupt")
            {
                Assert.IsNull(resolution); Assert.AreEqual(MergeSessionState.Resolving, session.State);
                if (manual is not null) Assert.IsTrue(File.Exists(manual.Handle));
            }
            else
            {
                Assert.AreEqual(choice, resolution!.Choice);
                Assert.AreEqual(session.Plan.Revision, resolution.PlanRevision);
                Assert.AreEqual("new-conflict", resolution.ConflictId);
                Assert.AreEqual(MergeSessionState.Ready, session.State);
            }
            session = store.Update(session, MergeSessionState.Abandoned);
            Assert.IsEmpty(store.ActiveRoots());
            var sessionDirectory = store.SessionDirectory(session.Id); Directory.CreateDirectory(sessionDirectory);
            var retained = Path.Combine(sessionDirectory, "legacy-payload.zip"); File.WriteAllText(retained, "catalog-owned");
            File.WriteAllText(Path.Combine(sessionDirectory, "temporary"), "temporary");
            var catalog = new LocalReplicaCatalog(Config, 0, [new LocalReplicaCatalogEntry(RepresentationId.New(), LocalReplicaId.New(),
                LocalReplicaLocator.ControlledAbsolute(retained), DateTimeOffset.UtcNow)]);
            store.CleanupTerminalArtifacts(catalog);
            store.CleanupTerminalArtifacts(catalog);
            Assert.IsTrue(File.Exists(retained)); Assert.IsFalse(File.Exists(Path.Combine(sessionDirectory, "temporary")));
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + Path.Combine(store.Root, "sessions.db")))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery();
            }
            Assert.ThrowsExactly<InvalidDataException>(() => store.ActiveRoots());
            Assert.ThrowsExactly<InvalidDataException>(() => store.Load(session.Id));
            Assert.IsTrue(File.Exists(retained));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
