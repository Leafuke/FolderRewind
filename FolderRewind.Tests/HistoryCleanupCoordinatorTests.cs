using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryCleanupCoordinatorTests
{
    [TestMethod]
    public async Task MissingProtectedSourceDoesNotBlockIndependentSourceAndSharedDependenciesAreGrouped()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var other = SourceId.New();
        var broken = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "broken.txt"), "A"));
        var old = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "old.txt"), "B"), source: other);
        var latest = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "new.txt"), "C"), source: other);
        foreach (var node in f.Nodes) await f.RecordBackupAsync(node);
        await f.History.Annotations.SetPinAsync(new(HistoryAnnotationTargetKind.Version, broken.Version.VersionId.Value), true);
        File.Delete(broken.Entry.Locator.AbsolutePath);
        var report = await f.Cleanup().ExecuteAsync(1, 5, HistoryRetentionBenefitPolicy.SpaceFirst, false, "test");
        Assert.AreEqual("Partial", report!.Status);
        Assert.IsTrue(report.HasWarnings, "A partially blocked cleanup must warn the committed backup caller.");
        Assert.AreEqual("Blocked", report.Sources.Single(r => r.SourceId == f.Source.ToString()).Status);
        Assert.AreEqual("Completed", report.Sources.Single(r => r.SourceId == other.ToString()).Status);
        Assert.IsFalse(File.Exists(old.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(latest);

        var versions = f.Nodes.ToDictionary(n => n.Version.VersionId, n => n.Version);
        var graph = f.Nodes.ToDictionary(n => n.Representation.RepresentationId, n => n.Representation);
        var catalog = new LocalReplicaCatalog(f.Config, 0, f.Nodes.Select(n => n.Entry));
        Assert.HasCount(2, HistoryCleanupCoordinator.GroupSources(versions, graph, catalog));
        var original = latest.Representation;
        graph[original.RepresentationId] = new(original.RepresentationId, original.VersionId, RepresentationKind.CoreSmartDelta,
            original.Format, [broken.Representation.RepresentationId], MaterializationFidelity.Exact, null, null, original.RepresentationSpecificMetadata);
        Assert.HasCount(1, HistoryCleanupCoordinator.GroupSources(versions, graph, catalog), "Cross-source dependency must prevent isolation.");
        graph[original.RepresentationId] = original;
        var shared = new LocalReplicaCatalog(f.Config, 0, [broken.Entry,
            new(original.RepresentationId, LocalReplicaId.New(), broken.Entry.Locator, DateTimeOffset.UtcNow)]);
        Assert.HasCount(1, HistoryCleanupCoordinator.GroupSources(versions, graph, shared), "Shared physical archives must be grouped.");
    }

    [TestMethod]
    public async Task AutomaticNoBenefitCacheSkipsRebuildButInvalidatesAfterProtectionChange()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = await f.AddAsync(null, p =>
        {
            File.WriteAllBytes(Path.Combine(p, "keep.bin"), new byte[20000]);
            File.WriteAllBytes(Path.Combine(p, "change.bin"), new byte[80000]);
            foreach (var file in Directory.EnumerateFiles(p)) File.SetLastWriteTimeUtc(file, time);
        });
        var b = await f.AddAsync(a, p =>
        {
            File.WriteAllBytes(Path.Combine(p, "change.bin"), Enumerable.Repeat((byte)1, 80000).ToArray());
            File.SetLastWriteTimeUtc(Path.Combine(p, "change.bin"), time);
        });
        var c = await f.AddAsync(b, _ => { });
        foreach (var node in f.Nodes) await f.RecordBackupAsync(node);
        var pin = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, a.Version.VersionId.Value);
        await f.History.Annotations.SetPinAsync(pin, true);
        var archive = new HistoryChainRewriteCostTests.StoredArchiveBackend(f.Archive, true);
        var first = await f.Cleanup(archive).ExecuteAsync(1, 5, HistoryRetentionBenefitPolicy.SpaceFirst, true, "test");
        Assert.AreEqual("NoSpaceBenefit", first!.Sources.Single().Status);
        int count = archive.FullCount + archive.DeltaCount;
        Assert.IsGreaterThan(0, count);
        // A normal unchanged invocation creates a Run, but no new checkpoint or representation.
        var codec = new HistoryPackCodec();
        var run = new BackupRun(RunId.New(), f.Config, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            BackupInvocationKind.Automatic, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(f.Source, BackupRunSourceOutcome.Reused, c.Version.VersionId, [], null)], []);
        await f.History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(run)]));
        var second = await f.Cleanup(archive).ExecuteAsync(1, 5, HistoryRetentionBenefitPolicy.SpaceFirst, true, "test");
        Assert.IsNull(second, "Repeated evaluations must not overwrite the last meaningful report.");
        Assert.AreEqual(count, archive.FullCount + archive.DeltaCount);
        var bPin = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, b.Version.VersionId.Value);
        await f.History.Annotations.SetPinAsync(bPin, true);
        await f.History.Annotations.SetPinAsync(bPin, false);
        await f.Cleanup(archive).ExecuteAsync(1, 5, HistoryRetentionBenefitPolicy.SpaceFirst, true, "test");
        Assert.IsGreaterThan(count, archive.FullCount + archive.DeltaCount);
        await f.AssertRestoresAsync(a, b, c);
    }
}
