using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialMergeIndexUpdaterTests
{
    [TestMethod]
    [DataRow(2)]
    [DataRow(32)]
    public void ResolveClearAndUndoMatchFreshBuildWithoutReplacingOwner(int count)
    {
        using var fixture = new MergeDecisionIndexFixture(count); using var published = fixture.Build();
        var index = published.Index; var directory = published.DirectoryPath;
        foreach (var ordinal in new[] { 0, 1, 0 })
        {
            var conflict = fixture.Conflicts[ordinal];
            var previous = fixture.Store.GetConflict(fixture.Session, conflict.Id).Resolution;
            Apply(fixture.Resolve(ordinal, MergeResolutionChoice.Theirs));
            var beforeClear = fixture.Store.GetConflict(fixture.Session, conflict.Id).Resolution;
            Apply(fixture.Clear(ordinal));
            Apply(fixture.Set(new Dictionary<string, MergeResolution?> { [conflict.Id] = beforeClear }));
            Apply(fixture.Set(new Dictionary<string, MergeResolution?> { [conflict.Id] = previous }));
        }
        void Apply(MergeDecisionChange change)
        {
            var untouched = published.Index.Query("", "", 0, 1000).Where(row => !change.Ids.Contains(row.Id)).ToArray();
            published.Index.SelectItem(fixture.Conflicts[1].Id, false);
            var projected = 0;
            SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, row => { projected++; return MergeDecisionIndexFixture.Project(row); });
            Assert.AreSame(index, published.Index); Assert.AreEqual(directory, published.DirectoryPath);
            Assert.AreEqual(change.After.Revision, published.Snapshot.Revision); Assert.AreEqual(change.Ids.Length, projected);
            Assert.HasCount(0, published.Index.Selected());
            Assert.AreEqual(JsonSerializer.Serialize(untouched), JsonSerializer.Serialize(published.Index.Query("", "", 0, 1000).Where(row => !change.Ids.Contains(row.Id)).ToArray()));
            using var rebuilt = fixture.Build();
            Assert.AreEqual(MergeDecisionIndexFixture.Rows(rebuilt.Index), MergeDecisionIndexFixture.Rows(published.Index));
        }
    }

    [TestMethod]
    public void EligibilityRejectsStaleCrossSessionCrossPlanAndSkippedRevisions()
    {
        using var fixture = new MergeDecisionIndexFixture(8); using var published = fixture.Build();
        var change = fixture.Resolve(0, MergeResolutionChoice.Ours);
        Assert.IsTrue(SpatialMergeIndexUpdater.CanApply(published.Snapshot, change, fixture.Session));
        var invalid = new[]
        {
            change with { Before = change.Before with { Id = Guid.NewGuid() } },
            change with { After = change.After with { Id = Guid.NewGuid() } },
            change with { After = change.After with { Plan = change.After.Plan with { Revision = Guid.NewGuid() } } },
            change with { After = change.After with { Revision = change.After.Revision + 1 } },
            change with { Before = change.Before with { Revision = change.Before.Revision - 1 } },
            change with { Before = change.Before with { State = MergeSessionState.Preparing } },
            change with { After = change.After with { State = MergeSessionState.Stale } },
            change with { Ids = ImmutableArray<string>.Empty }
        };
        foreach (var item in invalid)
        {
            Assert.IsFalse(SpatialMergeIndexUpdater.CanApply(published.Snapshot, item, item.After));
            Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => SpatialMergeIndexUpdater.Apply(fixture.Store, published, item, MergeDecisionIndexFixture.Project));
        }
        Assert.IsFalse(SpatialMergeIndexUpdater.CanApply(published.Snapshot, change, change.After with { Revision = change.After.Revision + 1 }));
        Assert.IsFalse(SpatialMergeIndexUpdater.CanApply(published.Snapshot, null, fixture.Session));
    }

    [TestMethod]
    [DataRow("projection")]
    [DataRow("identity")]
    [DataRow("cancellation")]
    [DataRow("core-revision")]
    public void FailedApplyRollsBackRowsSelectionAndSnapshot(string failure)
    {
        using var fixture = new MergeDecisionIndexFixture(16); using var published = fixture.Build();
        var selected = fixture.Conflicts[1].Id; published.Index.SelectItem(selected, false);
        var original = MergeDecisionIndexFixture.Rows(published.Index); var snapshot = published.Snapshot;
        var resolutions = new Dictionary<string, MergeResolution?>();
        foreach (var ordinal in new[] { 0, 1 })
        {
            var conflict = fixture.Conflicts[ordinal];
            resolutions.Add(conflict.Id, new(fixture.Session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours));
        }
        var change = fixture.Set(resolutions); using var cancel = new CancellationTokenSource(); var projected = 0;
        SpatialWorkspaceRow Project(MergeChangeRecord row)
        {
            if (++projected == 2)
            {
                if (failure == "projection") throw new InvalidDataException("projection failed after a row was written");
                if (failure == "identity") return MergeDecisionIndexFixture.Project(row) with { Id = fixture.Conflicts[6].Id };
                if (failure == "cancellation") cancel.Cancel();
                if (failure == "core-revision") fixture.Resolve(6, MergeResolutionChoice.Theirs);
            }
            return MergeDecisionIndexFixture.Project(row);
        }
        void Apply() => SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, Project, cancel.Token);
        if (failure is "projection" or "identity") Assert.ThrowsExactly<InvalidDataException>(Apply);
        else if (failure == "cancellation") Assert.ThrowsExactly<OperationCanceledException>(Apply);
        else Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(Apply);
        Assert.AreEqual(snapshot, published.Snapshot);
        Assert.AreEqual(original, MergeDecisionIndexFixture.Rows(published.Index));
        CollectionAssert.AreEqual(new[] { selected }, published.Index.Selected());
        Assert.IsTrue(Directory.Exists(published.DirectoryPath));
        if (failure != "core-revision")
        {
            SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, MergeDecisionIndexFixture.Project);
            Assert.AreEqual(change.After.Revision, published.Snapshot.Revision);
            Assert.HasCount(0, published.Index.Selected());
            using var rebuilt = fixture.Build();
            Assert.AreEqual(MergeDecisionIndexFixture.Rows(rebuilt.Index), MergeDecisionIndexFixture.Rows(published.Index));
        }
    }

    [TestMethod]
    public void MissingRequestedIdentityCannotMutateOrAdvancePublishedIndex()
    {
        using var fixture = new MergeDecisionIndexFixture(16); using var published = fixture.Build();
        var original = MergeDecisionIndexFixture.Rows(published.Index); var snapshot = published.Snapshot;
        var change = fixture.Resolve(0, MergeResolutionChoice.Ours) with { Ids = ["missing-from-plan"] };
        Assert.ThrowsExactly<KeyNotFoundException>(() => SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, MergeDecisionIndexFixture.Project));
        Assert.AreEqual(snapshot, published.Snapshot);
        Assert.AreEqual(original, MergeDecisionIndexFixture.Rows(published.Index));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrecommitFailureOrCancellationRestoresSelectionAndMutationVersion(bool cancelAtValidation)
    {
        using var fixture = new MergeDecisionIndexFixture(16); var diagnostics = new SpatialIndexDiagnostics();
        using var published = fixture.BuildMetered(diagnostics); var original = MergeDecisionIndexFixture.Rows(published.Index);
        published.Index.SelectItem(fixture.Conflicts[1].Id, false);
        using var cancel = new CancellationTokenSource();
        var row = published.Index.Query("", "", 0).First() with { Title = "changed before precommit validation" };
        void Update() => published.Index.UpdateMergeRows([row], 16, () =>
        {
            if (cancelAtValidation) cancel.Cancel(); else throw new InvalidOperationException("validation rejected");
        }, cancel.Token);
        if (cancelAtValidation) Assert.ThrowsExactly<OperationCanceledException>(Update);
        else Assert.ThrowsExactly<InvalidOperationException>(Update);
        Assert.AreEqual(original, MergeDecisionIndexFixture.Rows(published.Index));
        CollectionAssert.AreEqual(new[] { fixture.Conflicts[1].Id }, published.Index.Selected());
        var operation = diagnostics.Operations.Last(sample => sample.Kind == "merge-update");
        Assert.AreEqual(operation.BeginVersion, operation.EndVersion);
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(1024)]
    public void SingleRowUpdateHasBoundedCommandsAndReadsOnlyTheRequestedId(int count)
    {
        using var fixture = new MergeDecisionIndexFixture(count); var diagnostics = new SpatialIndexDiagnostics();
        using var published = fixture.BuildMetered(diagnostics); var change = fixture.Resolve(0, MergeResolutionChoice.Ours);
        var projected = new List<string>();
        SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, record => { projected.Add(record.Conflict.Id); return MergeDecisionIndexFixture.Project(record); });
        CollectionAssert.AreEqual(change.Ids.ToArray(), projected.ToArray());
        var operation = diagnostics.Operations.Last(sample => sample.Kind == "merge-update");
        Assert.IsLessThanOrEqualTo(8, operation.Commands, "One existing spatial row plus selection clear must use bounded SQL, independently of total rows.");
        Assert.AreEqual(operation.BeginVersion + 1, operation.EndVersion);
        Assert.AreEqual(count, published.Index.Counts().Total);
    }
}
