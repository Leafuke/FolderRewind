using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialMergeIndexBuilderTests
{
    [TestMethod]
    public async Task LongLogicalRootUsesWorkingSqliteJournalsAndLiveOwnersRemainIndependent()
    {
        using var fixture = new Fixture(1);
        var logicalRoot = Path.Combine(fixture.Root, new string('p', 150), new string('q', 80));
        Assert.IsGreaterThan(230, logicalRoot.Length);
        var protectedRoots = new[] { Path.Combine(fixture.Root, "source") };
        var first = await SpatialWorkspaceStorage.CreateAsync(logicalRoot, protectedRoots);
        OwnedPluginTemporaryStorage? second = null;
        SpatialMergeIndexBuild? firstIndex = null, secondIndex = null;
        string? firstDirectory = null, secondDirectory = null;
        try
        {
            firstDirectory = await first.CreateDirectoryAsync(default);
            firstIndex = SpatialMergeIndexBuilder.Build(fixture.Store, fixture.Session, firstDirectory, Project);
            Assert.AreEqual(1, firstIndex.Index.Counts().Total);
            Assert.IsLessThan(logicalRoot.Length, firstIndex.DirectoryPath.Length);
            second = await SpatialWorkspaceStorage.CreateAsync(logicalRoot, protectedRoots);
            Assert.IsTrue(Directory.Exists(firstDirectory), "Creating another live owner must not reclaim the first owner's index.");
            secondDirectory = await second.CreateDirectoryAsync(default);
            secondIndex = SpatialMergeIndexBuilder.Build(fixture.Store, fixture.Session, secondDirectory, Project);
            firstIndex.Dispose(); firstIndex = null;
            await first.DeleteAsync();
            Assert.IsFalse(Directory.Exists(firstDirectory));
            Assert.AreEqual(1, secondIndex.Index.Counts().Total, "Closing the first page must not remove the second page's database.");
            secondIndex.Dispose(); secondIndex = null;
            await second.DeleteAsync();
            Assert.IsFalse(Directory.Exists(secondDirectory));
            Assert.IsFalse(Directory.Exists(logicalRoot), "No long physical directory is created at the logical destination.");
        }
        finally
        {
            firstIndex?.Dispose(); secondIndex?.Dispose();
            await first.DeleteAsync(); if (second is not null) await second.DeleteAsync();
        }
    }

    [TestMethod]
    [DataRow("same")]
    [DataRow("ancestor")]
    [DataRow("descendant")]
    public async Task ShortPhysicalRootCannotOverlapAProtectedDirectory(string relation)
    {
        var logicalRoot = Path.Combine(Environment.CurrentDirectory, "artifacts", "storage-boundary-" + Guid.NewGuid().ToString("N"));
        var physical = OwnedPluginTemporaryStorage.ForLogicalRoot(logicalRoot);
        var protectedRoot = relation switch { "ancestor" => Path.GetTempPath(), "descendant" => Path.Combine(physical, "source"), _ => physical };
        Assert.IsTrue(BackupPathOverlapPolicy.Validate(logicalRoot, protectedRoot).IsSafe, "This fixture isolates the new physical-root boundary check.");
        await Assert.ThrowsExactlyAsync<IOException>(() => SpatialWorkspaceStorage.CreateAsync(logicalRoot, [protectedRoot]));
        Assert.IsFalse(Directory.Exists(physical));
    }

    [TestMethod]
    public async Task LogicalRootStillCannotOverlapAProtectedDirectory()
    {
        using var fixture = new Fixture(0);
        var logicalRoot = Path.Combine(fixture.Root, "source", "preview");
        await Assert.ThrowsExactlyAsync<IOException>(() => SpatialWorkspaceStorage.CreateAsync(logicalRoot, [Path.Combine(fixture.Root, "source")]));
        Assert.IsFalse(Directory.Exists(OwnedPluginTemporaryStorage.ForLogicalRoot(logicalRoot)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1000)]
    [DataRow(1001)]
    public void CompleteSnapshotBuildsAllRowsAndStartsWithNoSelection(int count)
    {
        using var fixture = new Fixture(count);
        var progress = new List<(long Written, int? Total)>();
        using var built = SpatialMergeIndexBuilder.Build(fixture.Store, fixture.Session, fixture.Candidates, Project,
            progress: (written, total) => progress.Add((written, total)));
        Assert.AreEqual(count, built.Snapshot.Total);
        Assert.AreEqual(count, built.Index.Counts().Total);
        Assert.HasCount(0, built.Index.Selected());
        Assert.AreEqual((0L, (int?)count), progress[0]);
        Assert.IsTrue(progress.All(p => p.Total == count));
        Assert.IsTrue(progress.Zip(progress.Skip(1)).All(p => p.Second.Written - p.First.Written <= 1000));
        Assert.HasCount(Math.Min(100, count), built.Index.Query("", "", 0));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CanceledOrFailedCandidateLeavesPublishedRowsAndSelectionUntouched(bool fail)
    {
        using var fixture = new Fixture(1001);
        using var old = new SpatialWorkspaceIndex(Path.Combine(fixture.Root, "published.db"));
        old.Put([new("published", "Published", "", "Unresolved", true, [])]); old.SelectItem("published", false);
        using var cancellation = new CancellationTokenSource();
        var projected = 0;
        SpatialWorkspaceRow Map(MergeChangeRecord row)
        {
            if (fail && ++projected == 10) throw new InvalidDataException("projection failed");
            return Project(row);
        }
        void Build() => SpatialMergeIndexBuilder.Build(fixture.Store, fixture.Session, fixture.Candidates, Map, cancellation.Token,
            (written, _) => { if (written >= 1000) cancellation.Cancel(); });
        if (fail) Assert.ThrowsExactly<InvalidDataException>(Build);
        else Assert.ThrowsExactly<OperationCanceledException>(Build);
        Assert.AreEqual("Published", old.Query("", "", 0).Single().Title);
        CollectionAssert.AreEqual(new[] { "published" }, old.Selected());
        Assert.IsFalse(Directory.Exists(fixture.Candidates) && Directory.EnumerateFileSystemEntries(fixture.Candidates).Any());
    }

    [TestMethod]
    public void AResolutionAfterBuildIsRejectedBeforePublication()
    {
        using var fixture = new Fixture(1);
        using var built = SpatialMergeIndexBuilder.Build(fixture.Store, fixture.Session, fixture.Candidates, Project);
        var conflict = fixture.Store.ConflictPage(fixture.Session, "").Single().Conflict;
        fixture.Store.ResolveBatch(fixture.Session, [new(fixture.Session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours)]);
        Assert.ThrowsExactly<MergeChangeSnapshotStaleException>(() => SpatialMergeIndexBuilder.RequireCurrent(fixture.Store, fixture.Session, built.Snapshot));
        Assert.AreEqual(1, built.Index.Counts().Total, "A failed publication check does not mutate the candidate or prior index.");
    }

    [TestMethod]
    public void ProjectionRetainsLockedBlockedAutomaticAndWholeDomainSemantics()
    {
        using var fixture = new Fixture(1);
        var original = fixture.Store.ConflictPage(fixture.Session, "").Single().Conflict;
        var label = new LocalizedText("Chunk", new Dictionary<string, string>());
        MergeSubject Subject(bool automatic, bool locked = false, bool blocked = false) => new("slot", "group", "fixed", label, label,
            blocked || locked ? [] : [MergeDecisionKind.Ours], automatic, JsonSerializer.SerializeToElement(1))
        { CanOverrideAutomatic = automatic && !locked, IsBlocked = blocked, SpatialLocations = [new("world", null, null)] };
        var locked = Project(new(original with { SemanticSubject = Subject(true, locked: true) }, null, true));
        var blocked = Project(new(original with { SemanticSubject = Subject(false, blocked: true) }, null, false));
        var automatic = Project(new(original with { SemanticSubject = Subject(true) },
            new(fixture.Session.Plan.Revision, original.Id, original.InputSignature, MergeResolutionChoice.Automatic), true));
        Assert.AreEqual("Locked", locked.Status); Assert.IsFalse(locked.CanSelect);
        Assert.AreEqual("Blocked", blocked.Status); Assert.IsFalse(blocked.CanSelect);
        Assert.AreEqual("Automatic", automatic.Status); Assert.IsTrue(automatic.CanSelect);
        Assert.IsNull(automatic.Locations.Single().Bounds);
    }

    private static SpatialWorkspaceRow Project(MergeChangeRecord row) => SpatialMergeRows.Create(row, t => t.Default, key => key);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "fr-merge-index-" + Guid.NewGuid().ToString("N"));
        internal string Candidates => Path.Combine(Root, "candidates");
        internal MergeSessionStore Store { get; }
        internal MergeSession Session { get; }
        internal Fixture(int count)
        {
            Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "local"));
            var source = SourceId.New(); var version = VersionId.New(); var checkpoint = CheckpointId.New();
            var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
            var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
            var identity = new CheckpointSource(source, new("world", Path.Combine(Root, "world")), version, CheckpointSourceDisposition.Captured);
            var descriptor = new GenericFileMergeProvider().Descriptor;
            var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, ours, theirs, checkpoint,
                new(new HistoryConfigId("merge-index"), 0, Array.Empty<WorkspaceSourceBaseline>()), "fixture", [],
                [new(source, identity, identity, identity, HistoryMergeSourceAction.MergeFiles, null)], descriptor.Identity, descriptor.PolicyIdentity);
            var session = Store.Create(plan, [version]);
            var conflicts = Enumerable.Range(0, count).Select(i => new MergeConflict("row-" + i.ToString("D5"), new(source, ["file-" + i]),
                MergeConflictKind.ModifyModify, "signature-" + i, ImmutableSortedDictionary<string, MergeFileValue>.Empty,
                ImmutableSortedDictionary<string, MergeFileValue>.Empty, ImmutableSortedDictionary<string, MergeFileValue>.Empty)).ToArray();
            Store.SaveSource(session, new(plan.Sources.Single(), MergeTreeManifest.Empty, MergeTreeManifest.Empty, MergeTreeManifest.Empty, MergeTreeManifest.Empty), conflicts);
            Session = Store.CompletePreparation(session, [version]);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
