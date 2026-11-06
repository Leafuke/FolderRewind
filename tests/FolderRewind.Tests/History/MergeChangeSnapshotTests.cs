using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MergeChangeSnapshotTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1000)]
    [DataRow(1001)]
    [DataRow(2001)]
    public void StreamsEveryRowInBoundedKeyOrderAndReportsOneFixedTotal(int count)
    {
        using var fixture = new Fixture();
        var session = fixture.Complete(generic: Enumerable.Range(0, count)
            .Select(i => fixture.Generic($"row-{i:D5}", $"path-{count - i:D5}")));
        var rows = new List<MergeChangeRecord>();
        var sizes = new List<int>();
        var startedCalls = 0;
        var total = -1;
        var snapshot = fixture.Store.ExportChangesSnapshot(session, batch =>
        {
            Assert.AreEqual(count, total);
            sizes.Add(batch.Count); rows.AddRange(batch);
        }, started: value => { startedCalls++; total = value; });

        Assert.AreEqual(1, startedCalls);
        Assert.AreEqual(new MergeChangeSnapshot(session.Id, session.Plan.Revision, session.Revision, count), snapshot);
        Assert.HasCount(count, rows);
        Assert.IsTrue(sizes.All(size => size is > 0 and <= 1000));
        Assert.HasCount((count + 999) / 1000, sizes);
        CollectionAssert.AreEqual(Enumerable.Range(0, count).Select(i => $"row-{i:D5}").ToArray(), rows.Select(r => r.Conflict.Id).ToArray());
        if (count > 1)
            Assert.AreEqual($"path-{count:D5}", rows[0].Conflict.Subject.Paths[0],
                "Export is in key order; presentation sorting belongs to the rebuilt index.");
    }

    [TestMethod]
    public void IncludesGenericChangesAndPreservesAutomaticClassificationAfterOverrides()
    {
        using var fixture = new Fixture();
        var overridden = fixture.Semantic("semantic-override", automatic: true);
        var genericResolved = fixture.Generic("generic-resolved");
        var session = fixture.Complete(
            [fixture.Semantic("semantic-auto", automatic: true), overridden, fixture.Semantic("semantic-unresolved"), fixture.Semantic("semantic-blocked", blocked: true)],
            [fixture.Generic("generic-display") with { AutomaticDisplayOnly = true }, fixture.Generic("generic-unresolved"), genericResolved]);
        session = fixture.Store.ResolveBatch(session,
            [new(session.Plan.Revision, overridden.Id, overridden.InputSignature, MergeResolutionChoice.Ours),
             new(session.Plan.Revision, genericResolved.Id, genericResolved.InputSignature, MergeResolutionChoice.Theirs)]);

        var expected = fixture.Store.QuerySemanticChanges(session, "", "All", 0, 100).Rows.ToDictionary(r => r.Conflict.Id);
        var rows = new List<MergeChangeRecord>();
        var snapshot = fixture.Store.ExportChangesSnapshot(session, rows.AddRange);
        Assert.AreEqual(7, snapshot.Total);
        foreach (var row in rows)
        {
            Assert.AreEqual(expected[row.Conflict.Id].Resolution, row.Resolution);
            Assert.AreEqual(expected[row.Conflict.Id].Automatic, row.Automatic);
        }
        CollectionAssert.AreEquivalent(new[] { "generic-display", "semantic-auto" }, rows.Where(r => r.Automatic).Select(r => r.Conflict.Id).ToArray());
        Assert.IsNull(rows.Single(r => r.Conflict.Id == "generic-display").Conflict.SemanticSubject);
        Assert.IsTrue(rows.Single(r => r.Conflict.Id == overridden.Id).Conflict.SemanticSubject!.IsAutomatic);
        Assert.IsFalse(rows.Single(r => r.Conflict.Id == overridden.Id).Automatic);

        var selected = fixture.Store.ReadChangesSnapshot(session, ["semantic-override", "generic-display", "semantic-auto", "semantic-override"]);
        Assert.HasCount(3, selected);
        foreach (var row in selected)
            Assert.AreEqual(JsonSerializer.Serialize(rows.Single(r => r.Conflict.Id == row.Conflict.Id)), JsonSerializer.Serialize(row));
    }

    [TestMethod]
    public void RequestedChangesAreDeduplicatedWithoutReadingUnrequestedOrCaseDistinctRows()
    {
        using var fixture = new Fixture();
        var ids = new[] { "Case", "case", "quoted'\\\"id", "汉字/path" };
        var session = fixture.Complete(generic: ids.Append("unrequested").Select(id => fixture.Generic(id)));
        var rows = fixture.Store.ReadChangesSnapshot(session, [ids[3], ids[1], ids[0], ids[2], ids[0], ids[3]]);
        CollectionAssert.AreEqual(ids.Order(StringComparer.Ordinal).ToArray(), rows.Select(r => r.Conflict.Id).ToArray());
        Assert.IsTrue(rows.All(r => r.Resolution is null && !r.Automatic));
        Assert.HasCount(0, fixture.Store.ReadChangesSnapshot(session, []));
        fixture.Store.RequireChangesSnapshotCurrent(session);
    }

    [TestMethod]
    public void RequestedChangesSupportLargeDeduplicatedIdentitySets()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 1101).Select(i => $"row-{i:D5}").ToArray();
        var session = fixture.Complete(generic: ids.Select(id => fixture.Generic(id)));
        var selected = ids.Where((_, i) => i % 2 == 0).Concat(ids).ToArray();
        var rows = fixture.Store.ReadChangesSnapshot(session, selected);
        Assert.HasCount(ids.Length, rows);
        CollectionAssert.AreEqual(ids, rows.Select(r => r.Conflict.Id).ToArray());
    }

    [TestMethod]
    public void MissingRequestedChangesRejectTheWholeReadAndReleaseItBeforeLaterWrites()
    {
        using var fixture = new Fixture();
        var conflict = fixture.Generic("present");
        var session = fixture.Complete(generic: [conflict]);
        Assert.ThrowsExactly<KeyNotFoundException>(() => fixture.Store.ReadChangesSnapshot(session, [conflict.Id, "missing", "missing"]));
        fixture.Store.RequireChangesSnapshotCurrent(session);
        session = fixture.Store.Resolve(session, new(session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Theirs));
        Assert.AreEqual(MergeResolutionChoice.Theirs, fixture.Store.ReadChangesSnapshot(session, [conflict.Id]).Single().Resolution!.Choice);
    }

    [TestMethod]
    public void CurrentSnapshotGuardRejectsAResolutionOrReplanCommittedBeforeIndexPublication()
    {
        using var fixture = new Fixture();
        var conflict = fixture.Generic("row");
        var session = fixture.Complete(generic: [conflict]);
        var selected = fixture.Store.ReadChangesSnapshot(session, [conflict.Id]);
        Assert.IsNull(selected.Single().Resolution);
        var resolved = fixture.Store.Resolve(session, new(session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours));
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.RequireChangesSnapshotCurrent(session));
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ReadChangesSnapshot(session, [conflict.Id]));
        fixture.Store.RequireChangesSnapshotCurrent(resolved);

        var replanned = fixture.Store.Replan(resolved, resolved.Plan with { Revision = Guid.NewGuid() }, [fixture.Version]);
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.RequireChangesSnapshotCurrent(resolved));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.RequireChangesSnapshotCurrent(replanned));
    }

    [TestMethod]
    public void RequestedReadCancellationAndCanceledFormerTokensCannotCancelLaterWrites()
    {
        using var fixture = new Fixture();
        var conflict = fixture.Generic("row");
        var session = fixture.Complete(generic: [conflict]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var error = Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Store.ReadChangesSnapshot(session, [conflict.Id], canceled.Token));
        Assert.AreEqual(canceled.Token, error.CancellationToken);
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Store.RequireChangesSnapshotCurrent(session, canceled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Store.ReadChangesSnapshot(session, [], canceled.Token));

        using var previous = new CancellationTokenSource();
        Assert.HasCount(1, fixture.Store.ReadChangesSnapshot(session, [conflict.Id], previous.Token));
        previous.Cancel();
        session = fixture.Store.Resolve(session, new(session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours));
        fixture.Store.RequireChangesSnapshotCurrent(session);
        Assert.AreEqual(MergeResolutionChoice.Ours, fixture.Store.ReadChangesSnapshot(session, [conflict.Id]).Single().Resolution!.Choice);
    }

    [TestMethod]
    public void RejectsPreparingAndStaleSessionsWithoutStartingTheConsumer()
    {
        using var fixture = new Fixture();
        var calls = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ExportChangesSnapshot(fixture.Session, _ => calls++));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadChangesSnapshot(fixture.Session, []));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.RequireChangesSnapshotCurrent(fixture.Session));
        var ready = fixture.Complete();
        var stale = fixture.Store.Update(ready, MergeSessionState.Stale);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ExportChangesSnapshot(stale, _ => calls++));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadChangesSnapshot(stale, []));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.RequireChangesSnapshotCurrent(stale));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void RejectsAnOutdatedIdentityBeforeReportingOrConsumingAnyRows()
    {
        using var fixture = new Fixture();
        var session = fixture.Complete(generic: [fixture.Generic("unresolved")]);
        var calls = 0;
        foreach (var obsolete in new[]
        {
            session with { Id = Guid.NewGuid() }, session with { Revision = session.Revision + 1 },
            session with { Plan = session.Plan with { Revision = Guid.NewGuid() } }, session with { State = MergeSessionState.Ready }
        })
        {
            Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ExportChangesSnapshot(obsolete,
                _ => calls++, started: _ => calls++));
            Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ReadChangesSnapshot(obsolete, ["unresolved"]));
            Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ReadChangesSnapshot(obsolete, []));
            Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.RequireChangesSnapshotCurrent(obsolete));
        }
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void AResolutionDuringConsumptionKeepsTheOldReadSnapshotAndRejectsPublication()
    {
        using var fixture = new Fixture();
        var conflicts = Enumerable.Range(0, 2001).Select(i => fixture.Generic($"row-{i:D5}")).ToArray();
        var session = fixture.Complete(generic: conflicts);
        var rows = new List<MergeChangeRecord>();
        MergeSession? updated = null;
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ExportChangesSnapshot(session, batch =>
        {
            rows.AddRange(batch);
            updated ??= fixture.Store.Resolve(session,
                new(session.Plan.Revision, conflicts[^1].Id, conflicts[^1].InputSignature, MergeResolutionChoice.Theirs));
        }));
        Assert.HasCount(2001, rows);
        Assert.IsTrue(rows.All(r => r.Resolution is null), "Later batches must not mix the concurrently committed resolution into the old snapshot.");
        var refreshed = new List<MergeChangeRecord>();
        var latest = fixture.Store.ExportChangesSnapshot(updated!, refreshed.AddRange);
        Assert.AreEqual(updated!.Revision, latest.Revision);
        Assert.AreEqual(MergeResolutionChoice.Theirs, refreshed.Single(r => r.Conflict.Id == conflicts[^1].Id).Resolution!.Choice);
    }

    [TestMethod]
    public void AReplanDuringConsumptionCannotMixItsNewRowsIntoTheOldPlan()
    {
        using var fixture = new Fixture();
        var session = fixture.Complete(generic: Enumerable.Range(0, 1001).Select(i => fixture.Generic($"old-{i:D5}")));
        var rows = new List<MergeChangeRecord>();
        MergeSession? replacement = null;
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ExportChangesSnapshot(session, batch =>
        {
            rows.AddRange(batch);
            if (replacement is not null) return;
            replacement = fixture.Store.Replan(session, session.Plan with { Revision = Guid.NewGuid() }, [fixture.Version]);
            fixture.SaveGeneric(replacement, [fixture.Generic("new-plan")]);
            replacement = fixture.Store.CompletePreparation(replacement, [fixture.Version]);
        }));
        Assert.HasCount(1001, rows);
        Assert.IsTrue(rows.All(r => r.Conflict.Id.StartsWith("old-", StringComparison.Ordinal)));
        var latestRows = new List<MergeChangeRecord>();
        var latest = fixture.Store.ExportChangesSnapshot(replacement!, latestRows.AddRange);
        Assert.AreEqual(replacement!.Plan.Revision, latest.PlanRevision);
        Assert.AreEqual("new-plan", latestRows.Single().Conflict.Id);
    }

    [TestMethod]
    public void AChangeInTheFinalBatchIsDetectedByTheFreshValidationRead()
    {
        using var fixture = new Fixture();
        var conflict = fixture.Generic("last");
        var session = fixture.Complete(generic: [conflict]);
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => fixture.Store.ExportChangesSnapshot(session, _ =>
            fixture.Store.Resolve(session, new(session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours))));
    }

    [TestMethod]
    public void CancellationBeforeOrDuringConsumptionReleasesTheReadBeforeLaterWrites()
    {
        using var fixture = new Fixture();
        var conflicts = Enumerable.Range(0, 1001).Select(i => fixture.Generic($"row-{i:D5}")).ToArray();
        var session = fixture.Complete(generic: conflicts);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var callbacks = 0;
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Store.ExportChangesSnapshot(session,
            _ => callbacks++, canceled.Token, _ => callbacks++));
        Assert.AreEqual(0, callbacks);

        using var cancellation = new CancellationTokenSource();
        var received = 0;
        var error = Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Store.ExportChangesSnapshot(session, batch =>
        { received += batch.Count; cancellation.Cancel(); }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(1000, received);
        session = fixture.Store.Resolve(session,
            new(session.Plan.Revision, conflicts[0].Id, conflicts[0].InputSignature, MergeResolutionChoice.Ours));
        var rows = new List<MergeChangeRecord>();
        Assert.AreEqual(1001, fixture.Store.ExportChangesSnapshot(session, rows.AddRange).Total);
        Assert.AreEqual(MergeResolutionChoice.Ours, rows[0].Resolution!.Choice);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConsumerFailuresReleaseTheTransactionAndDoNotLeaveCancellationInstalled(bool failWhenStarting)
    {
        using var fixture = new Fixture();
        var conflict = fixture.Generic("row");
        var session = fixture.Complete(generic: [conflict]);
        var failure = new InvalidOperationException("consumer stopped");
        using var cancellation = new CancellationTokenSource();
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ExportChangesSnapshot(session,
            _ => { if (!failWhenStarting) throw failure; }, cancellation.Token,
            _ => { if (failWhenStarting) throw failure; }));
        Assert.AreSame(failure, error);
        cancellation.Cancel();
        session = fixture.Store.Resolve(session, new(session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Theirs));
        Assert.AreEqual(1, fixture.Store.ExportChangesSnapshot(session, _ => { }).Total);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fr-merge-snapshot-" + Guid.NewGuid().ToString("N"));
        internal MergeSessionStore Store { get; }
        internal SourceId Source { get; } = SourceId.New();
        internal VersionId Version { get; } = VersionId.New();
        internal MergeSession Session { get; }
        internal Fixture()
        {
            Store = new(_root);
            var checkpoint = CheckpointId.New();
            var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: Source);
            var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: Source);
            var identity = new CheckpointSource(Source, new("world", "world"), Version, CheckpointSourceDisposition.Captured);
            var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, ours, theirs, checkpoint,
                new(new HistoryConfigId("merge-snapshot-test"), 0, Array.Empty<WorkspaceSourceBaseline>()), "config", [],
                [new(Source, identity, identity, identity, HistoryMergeSourceAction.MergeFiles, null)]);
            Session = Store.Create(plan, [Version]);
        }
        internal MergeConflict Generic(string id, string? path = null)
            => new(id, new(Source, [path ?? id]), MergeConflictKind.ModifyModify, "input-" + id,
                MergeTreeManifest.Empty.Files, MergeTreeManifest.Empty.Files, MergeTreeManifest.Empty.Files);
        internal MergeConflict Semantic(string id, bool automatic = false, bool blocked = false)
        {
            var text = new LocalizedText(id, new Dictionary<string, string>());
            return Generic(id) with
            {
                SemanticSubject = new(id, "group", "input-" + id, text, text,
                    blocked ? [] : [MergeDecisionKind.Ours, MergeDecisionKind.Theirs], automatic, JsonSerializer.SerializeToElement(new { }))
                { IsBlocked = blocked, CanOverrideAutomatic = automatic }
            };
        }
        internal MergeSession Complete(IEnumerable<MergeConflict>? semantic = null, IEnumerable<MergeConflict>? generic = null)
        {
            if (semantic is not null) Store.AddSemanticConflicts(Session, semantic.ToArray());
            SaveGeneric(Session, generic ?? []);
            return Store.CompletePreparation(Session, [Version]);
        }
        internal void SaveGeneric(MergeSession session, IEnumerable<MergeConflict> conflicts)
            => Store.SaveSource(session, new(session.Plan.Sources.Single(), MergeTreeManifest.Empty, MergeTreeManifest.Empty,
                MergeTreeManifest.Empty, MergeTreeManifest.Empty), conflicts, retainSemantic: true);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
