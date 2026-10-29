using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeMetadataTests
{
    private static VersionMetadataSnapshot Snapshot(SourceVersion version)
        => new VersionMetadataCandidate("com.test.metadata", "world", 1,
            JsonSerializer.SerializeToElement(new { levelName = "Merged World" })).ToSnapshot(version);

    private static HistoryMergeCommitBuilder Builder(HistoryMergeArchiveFixture fixture)
        => new(fixture.History, fixture.Restore(), fixture.Archives, fixture.Archives,
            (version, _, _) => Task.FromResult<IReadOnlyList<VersionMetadataSnapshot>>([Snapshot(version)]));

    [TestMethod]
    public async Task MetadataUsesVersionTimeSurvivesRestartAndCommitsWithMergedArchive()
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var prepared = await Builder(fixture).BuildAsync(fixture.Session);
        var version = prepared.Sources.Single().Version;
        var metadata = prepared.Facts.OfType<VersionMetadataSnapshot>().Single();
        Assert.AreEqual(version.CreatedAtUtc, metadata.CapturedAtUtc);
        Assert.AreEqual(version.VersionId, metadata.VersionId);
        await fixture.ReopenAsync();
        var reopened = await Builder(fixture).BuildAsync(fixture.Session);
        Assert.AreEqual(prepared.PackId, reopened.PackId);
        Assert.AreEqual(1, fixture.Archives.Created);
        var result = await new HistoryMergeApplyService(fixture.History, fixture.Restore(), Builder(fixture))
            .ApplyAsync(fixture.Session, ready: reopened);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        var stored = (await fixture.History.Query.GetVersionMetadataSnapshotsAsync(version.VersionId)).Single();
        Assert.AreEqual(version.CreatedAtUtc, stored.CapturedAtUtc);
        Assert.AreEqual("Merged World", stored.Payload.GetProperty("levelName").GetString());
        await fixture.AssertMergedAndRestorableAsync(reopened);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidMetadataIsRejectedBeforePreparationIsPublished(bool orphan)
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var packCount = (await fixture.History.Repository.ReadAllPacksAsync()).Count;
        var builder = new HistoryMergeCommitBuilder(fixture.History, fixture.Restore(), fixture.Archives, fixture.Archives,
            (version, _, _) => Task.FromResult<IReadOnlyList<VersionMetadataSnapshot>>([
                VersionMetadataSnapshot.Create(orphan ? VersionId.New() : version.VersionId,
                    "com.test.metadata", "world", 1, Snapshot(version).Payload, version.CreatedAtUtc.AddSeconds(1))]));
        var error = await Assert.ThrowsExactlyAsync<HistoryRepositoryValidationException>(() => builder.BuildAsync(fixture.Session));
        Assert.AreEqual("validate-facts", error.Data["MergeStage"]);
        Assert.IsNull(fixture.History.MergeSessions.LoadPrepared(fixture.Session));
        Assert.HasCount(packCount, await fixture.History.Repository.ReadAllPacksAsync());
        Assert.AreEqual(fixture.OursDigest, (await MergeTreeManifest.ReadAsync(fixture.Target, _ => true, default)).Digest);
    }

    [TestMethod]
    public async Task OldTimestampPreparationRegeneratesWithoutLosingDecisionsOrProtection()
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync((root, side) =>
        {
            if (side == 2) File.WriteAllText(Path.Combine(root, "a.txt"), "theirs-a");
        }, expectedState: MergeSessionState.Resolving);
        var store = fixture.History.MergeSessions;
        var service = new HistoryMergeService(fixture.History, fixture.Restore());
        var conflict = service.AllConflicts(fixture.Session).Single().Conflict;
        var session = store.Resolve(fixture.Session,
            new(fixture.Session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours));
        var prepared = await Builder(fixture).BuildAsync(session);
        var metadata = prepared.Facts.OfType<VersionMetadataSnapshot>().Single();
        var bad = VersionMetadataSnapshot.Create(metadata.VersionId, metadata.ProducerPluginId, metadata.SchemaId,
            metadata.SchemaVersion, metadata.Payload, metadata.CapturedAtUtc.AddMilliseconds(740));
        var original = store.LoadPrepared(session)!;
        store.DiscardPrepared(session, original);
        var old = store.SavePrepared(session, prepared with
        {
            Facts = [.. prepared.Facts.Select(f => f is VersionMetadataSnapshot ? bad : f)]
        });
        Assert.ThrowsExactly<InvalidOperationException>(() => store.DiscardPrepared(session, original));
        var protectedSession = store.RecordProtection(session, fixture.Before);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.DiscardPrepared(session, old));
        await fixture.ReopenAsync();
        session = fixture.Session;
        Assert.AreEqual(protectedSession.Revision, session.Revision);
        var oldPrepared = fixture.History.MergeSessions.LoadPrepared(session)!.Restore(session);
        Assert.IsTrue(HistoryMergeCommitBuilder.HasMismatchedMetadataCaptureTime(oldPrepared));
        var decisions = new HistoryMergeService(fixture.History, fixture.Restore()).AllConflicts(session)
            .Select(c => c.Resolution).ToArray();
        var replacement = await Builder(fixture).BuildAsync(session);
        Assert.AreNotEqual(oldPrepared.PackId, replacement.PackId);
        Assert.AreNotEqual(oldPrepared.Sources.Single().Version.VersionId, replacement.Sources.Single().Version.VersionId);
        Assert.AreEqual(oldPrepared.Sources.Single().TreeDigest, replacement.Sources.Single().TreeDigest);
        Assert.IsFalse(HistoryMergeCommitBuilder.HasMismatchedMetadataCaptureTime(replacement));
        CollectionAssert.AreEqual(decisions, new HistoryMergeService(fixture.History, fixture.Restore())
            .AllConflicts(session).Select(c => c.Resolution).ToArray());
        var current = fixture.History.MergeSessions.Load(session.Id);
        Assert.AreEqual(session.Revision, current.Revision);
        Assert.IsTrue(HistoryWorkspace.StateEquals(session.ProtectedWorkspace!, current.ProtectedWorkspace!));
        Assert.AreEqual(fixture.OursDigest, (await MergeTreeManifest.ReadAsync(fixture.Target, _ => true, default)).Digest);
        Assert.IsFalse(File.Exists(fixture.History.Repository.Paths.GetPackPath(oldPrepared.PackId)));
        var staleReview = await new HistoryMergeApplyService(fixture.History, fixture.Restore(), Builder(fixture))
            .ApplyAsync(session, ready: oldPrepared);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, staleReview.Status);
        Assert.IsFalse(staleReview.TargetCommitted);
        var retry = await new HistoryMergeApplyService(fixture.History, fixture.Restore(), Builder(fixture))
            .ApplyAsync(session, ready: replacement);
        Assert.IsTrue(retry.Succeeded, retry.Diagnostic);
        await fixture.AssertMergedAndRestorableAsync(replacement);
    }

    [TestMethod]
    public async Task OtherCachedMetadataIntegrityErrorsRemainBlocked()
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var prepared = await Builder(fixture).BuildAsync(fixture.Session);
        var bad = VersionMetadataSnapshot.Create(VersionId.New(), "com.test.metadata", "world", 1,
            Snapshot(prepared.Sources.Single().Version).Payload, DateTimeOffset.UnixEpoch);
        var store = fixture.History.MergeSessions;
        store.DiscardPrepared(fixture.Session, store.LoadPrepared(fixture.Session)!);
        store.SavePrepared(fixture.Session, prepared with
        {
            Facts = [.. prepared.Facts.Select(f => f is VersionMetadataSnapshot ? bad : f)]
        });
        await Assert.ThrowsExactlyAsync<HistoryRepositoryValidationException>(() => Builder(fixture).BuildAsync(fixture.Session));
        Assert.AreEqual(prepared.PackId, store.LoadPrepared(fixture.Session)!.Restore(fixture.Session).PackId);
        Assert.AreEqual(1, fixture.Archives.Created);
    }

    [TestMethod]
    public async Task PublishedPackWithMismatchedTimestampIsNeverRebuilt()
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var prepared = await Builder(fixture).BuildAsync(fixture.Session);
        var metadata = prepared.Facts.OfType<VersionMetadataSnapshot>().Single();
        var bad = VersionMetadataSnapshot.Create(metadata.VersionId, metadata.ProducerPluginId, metadata.SchemaId,
            metadata.SchemaVersion, metadata.Payload, metadata.CapturedAtUtc.AddSeconds(1));
        var store = fixture.History.MergeSessions;
        store.DiscardPrepared(fixture.Session, store.LoadPrepared(fixture.Session)!);
        var saved = store.SavePrepared(fixture.Session, prepared with
        {
            Facts = [.. prepared.Facts.Select(f => f is VersionMetadataSnapshot ? bad : f)]
        });
        var publishedPath = fixture.History.Repository.Paths.GetPackPath(prepared.PackId);
        Directory.CreateDirectory(Path.GetDirectoryName(publishedPath)!);
        await File.WriteAllBytesAsync(publishedPath, saved.PackBytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Builder(fixture).BuildAsync(fixture.Session));
        CollectionAssert.AreEqual(saved.PackBytes, await File.ReadAllBytesAsync(publishedPath));
        CollectionAssert.AreEqual(saved.PackBytes, store.LoadPrepared(fixture.Session)!.PackBytes);
        Assert.AreEqual(1, fixture.Archives.Created);
    }
}
