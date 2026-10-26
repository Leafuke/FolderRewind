using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.LocalState;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeArchiveTests
{
    [TestMethod]
    [DataRow("none")]
    [DataRow("base")]
    [DataRow("ours")]
    [DataRow("theirs")]
    public async Task GenericMergePreservesOnlyCompleteInputDirectoryEvidence(string incomplete)
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync((root, _) => Directory.CreateDirectory(Path.Combine(root, "empty")));
        var store = fixture.History.MergeSessions;
        var source = store.Sources(fixture.Session).Single();
        var session = store.Create(fixture.Session.Plan with { Revision = Guid.NewGuid() },
            [fixture.Base.VersionId, fixture.Ours.NewVersions.Single().VersionId, fixture.Theirs.NewVersions.Single().VersionId]);
        // Arrange the persisted, already-admitted input evidence; exercise real archive/commit/restore below.
        MergeTreeManifest CopyInput(MergeTreeManifest tree, string side)
        {
            var root = Path.Combine(store.SessionDirectory(session.Id), "inputs", side); Directory.CreateDirectory(root);
            foreach (var directory in tree.Directories) Directory.CreateDirectory(Path.Combine(root, directory));
            var files = tree.Files.ToDictionary(pair => pair.Key, pair =>
            {
                var path = Path.Combine(root, pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(pair.Value.Handle, path);
                return pair.Value with { Handle = path };
            });
            return MergeTreeManifest.Create(files, tree.Directories) with { HasCompleteDirectoryInventory = side != incomplete };
        }
        var b = CopyInput(source.Base, "base"); var o = CopyInput(source.Ours, "ours"); var t = CopyInput(source.Theirs, "theirs");
        var proposal = new GenericFileMergeProvider().Analyze(fixture.Source, b, o, t);
        store.SaveSource(session, new(source.Plan, proposal.Automatic, b, o, t), proposal.Conflicts);
        session = store.Update(session, MergeSessionState.Ready);
        var prepared = await fixture.Builder().BuildAsync(session);
        var expected = incomplete == "none";
        Assert.AreEqual(expected, prepared.Facts.OfType<VersionRepresentation>().Single().RepresentationSpecificMetadata.ContainsKey("mergeDirectoryEvidence"));
        var restore = fixture.Restore();
        var applied = await new HistoryMergeApplyService(fixture.History, restore, fixture.Builder(restore)).ApplyAsync(session, ready: prepared);
        Assert.IsTrue(applied.Succeeded, applied.Diagnostic);
        await fixture.ReopenAsync();
        var materialized = await fixture.Restore().PrepareSourceAsync(prepared.Sources.Single().Version,
            new(fixture.Source, Path.Combine(fixture.Root, "roundtrip")), MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, default);
        try
        {
            Assert.AreEqual(expected, materialized.HasCompleteDirectoryInventory);
            Assert.IsTrue(Directory.Exists(Path.Combine(materialized.StagingDirectory, "empty")));
            Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(materialized.StagingDirectory, "a.txt")));
            Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(materialized.StagingDirectory, "b.txt")));
        }
        finally { HistoryRestoreTransactionJournalStore.CleanupStaging([materialized.StagingDirectory]); }
    }

    [TestMethod]
    [DataRow("revision")]
    [DataRow("candidate")]
    [DataRow("working")]
    public async Task ReviewedMergeRejectsChangedIdentityOrWorkingFiles(string change)
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var prepared = await fixture.Builder().BuildAsync(fixture.Session);
        var live = await MergeTreeManifest.ReadAsync(fixture.Target, _ => true, default);
        var review = new MergeReviewSnapshot(prepared, [new(fixture.Source, fixture.Target, true, live.Digest)], [], [], false);
        if (change == "revision") review = review with { Prepared = prepared with { Session = prepared.Session with { Revision = prepared.Session.Revision + 1 } } };
        if (change == "candidate") review = review with { Prepared = prepared with { PackId = PackId.New() } };
        if (change == "working") File.WriteAllText(Path.Combine(fixture.Target, "a.txt"), "new unbacked work");
        var packCount = (await fixture.History.Repository.ReadAllPacksAsync()).Count;
        var restore = fixture.Restore();
        var result = await new HistoryMergeApplyService(fixture.History, restore, fixture.Builder(restore))
            .ApplyAsync(fixture.Session, ready: prepared, review: review);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status);
        Assert.IsFalse(result.TargetCommitted);
        Assert.HasCount(packCount, await fixture.History.Repository.ReadAllPacksAsync());
        Assert.AreEqual(change == "working" ? "new unbacked work" : "ours-a", File.ReadAllText(Path.Combine(fixture.Target, "a.txt")));
    }

    [TestMethod]
    public async Task IndependentChangesCreateNewArchivePersistPreparationAndRestoreExactly()
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var prepared = await fixture.Builder().BuildAsync(fixture.Session);
        var version = prepared.Sources.Single().Version;
        Assert.AreEqual(1, fixture.Archives.Created);
        Assert.AreEqual(1, fixture.Archives.DeepVerified);
        Assert.AreEqual(1, fixture.Archives.Materialized);
        Assert.AreEqual(SourceVersionCreationKind.Merge, version.CreationKind);
        Assert.AreEqual(CheckpointCreationKind.Merge, prepared.Checkpoint.CreationKind);
        CollectionAssert.AreEquivalent(new[] { fixture.Ours.NewVersions.Single().VersionId, fixture.Theirs.NewVersions.Single().VersionId },
            version.ParentVersionIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { fixture.Ours.NewCheckpoints.Single().CheckpointId, fixture.Theirs.NewCheckpoints.Single().CheckpointId },
            prepared.Checkpoint.ParentCheckpointIds.ToArray());
        var digest = prepared.Sources.Single().TreeDigest;
        foreach (var input in new[] { fixture.Base.VersionId, fixture.Ours.NewVersions.Single().VersionId, fixture.Theirs.NewVersions.Single().VersionId })
            Assert.AreNotEqual((await fixture.History.Query.GetVersionAsync(input))!.StateFingerprint, digest);
        var representation = prepared.Facts.OfType<VersionRepresentation>().Single();
        Assert.AreEqual(MaterializationFidelity.Exact, representation.Fidelity);
        Assert.IsTrue(representation.RepresentationSpecificMetadata.ContainsKey("storageSha256"));
        await fixture.ReopenAsync();
        var reloaded = await fixture.Builder().BuildAsync(fixture.Session);
        Assert.AreEqual(prepared.PackId, reloaded.PackId);
        Assert.AreEqual(prepared.TransactionId, reloaded.TransactionId);
        Assert.AreEqual(version.VersionId, reloaded.Sources.Single().Version.VersionId);
        Assert.AreEqual(1, fixture.Archives.Created, "Restart must reuse persisted preparation.");
        var restore = fixture.Restore();
        var result = await new HistoryMergeApplyService(fixture.History, restore, fixture.Builder(restore))
            .ApplyAsync(fixture.Session, ready: reloaded);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual(MergeSessionState.Committed, fixture.History.MergeSessions.Load(fixture.Session.Id).State);
        Assert.IsGreaterThanOrEqualTo(2, fixture.Archives.Materialized, "Apply must revalidate the archive.");
        await fixture.AssertMergedAndRestorableAsync(reloaded);
    }

    [TestMethod]
    [DataRow("verify")]
    [DataRow("roundtrip")]
    [DataRow("tamper")]
    public async Task InvalidNewArchiveCannotMutateFilesOrPublishHistory(string failure)
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var packCount = (await fixture.History.Repository.ReadAllPacksAsync()).Count;
        fixture.Archives.FailVerification = failure == "verify";
        fixture.Archives.ChangeRoundtrip = failure == "roundtrip";
        if (failure == "tamper")
        {
            var prepared = await fixture.Builder().BuildAsync(fixture.Session);
            File.AppendAllText(prepared.NewReplicas.Single().Locator.AbsolutePath, "tampered");
        }
        var restore = fixture.Restore();
        var result = await new HistoryMergeApplyService(fixture.History, restore, fixture.Builder(restore)).ApplyAsync(fixture.Session);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status, result.Diagnostic);
        Assert.IsFalse(result.TargetCommitted);
        Assert.IsFalse(result.WorkspaceUpdated);
        Assert.HasCount(packCount, await fixture.History.Repository.ReadAllPacksAsync());
        Assert.IsTrue(FolderRewind.History.LocalState.HistoryWorkspace.StateEquals(fixture.Before,
            (await fixture.History.WorkspaceStore.LoadAsync()).Value!));
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(fixture.Target, "a.txt")));
        Assert.AreEqual("base-b", File.ReadAllText(Path.Combine(fixture.Target, "b.txt")));
    }
}
