using System.Diagnostics;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryBrowsingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(12, 1, 1024L)]
    [DataRow(1200, 1, 1024L)]
    [DataRow(12, 1, 536870912L)]
    [DataRow(1200, 32, 1024L)]
    public async Task BrowsingUsesMetadataEvenWhenArchiveCannotBeOpened(int count, int branches, long archiveSize)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindBrowsingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            var sourceId = SourceId.New();
            var path = Path.Combine(root, "archive.7z");
            // Deliberately invalid and locked: browsing must not open, hash or test this archive.
            await using var archive = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            archive.SetLength(archiveSize);
            var codec = new HistoryPackCodec();
            var objects = new List<object>();
            var entries = new List<LocalReplicaCatalogEntry>();
            var checkpoints = new List<SourceCheckpoint>();
            for (var index = 0; index < count; index++)
            {
                var version = new SourceVersion(VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow.AddSeconds(index), null,
                    CaptureScope.FullSource, CaptureOutcome.Captured, [], new("Test source", root), null, HistoryProvenance.Native("test"));
                var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId,
                    RepresentationKind.CoreFull, "7z", [], MaterializationFidelity.Exact, new string('a', 64), null, []);
                var checkpoint = new SourceCheckpoint(CheckpointId.New(), configId, version.CreatedAtUtc, null,
                    HistoryProvenance.Native("test"), [new(sourceId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);
                objects.AddRange([version, representation, checkpoint]);
                checkpoints.Add(checkpoint);
                entries.Add(new(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow));
            }
            for (var index = 0; index < branches; index++)
                objects.Add(new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], $"branch-{index}", checkpoints[index % count].CheckpointId,
                    false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: checkpoints[index % count].SourceId));
            var paths = new HistoryRepositoryPaths(Path.Combine(root, "repository"));
            await using (var seed = new HistoryRuntime(new FileHistoryRepository(configId, paths)))
            {
                await seed.InitializeAsync();
                await seed.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, objects.Select(item => codec.CreateObject(item))));
                await seed.LocalReplicaCatalogStore.SaveAsync(new(configId, 0, entries), LocalReplicaCatalogStore.MissingRevision);
            }
            await using var runtime = new HistoryRuntime(new FileHistoryRepository(configId, paths));
            var timer = Stopwatch.StartNew();
            await runtime.InitializeAsync();
            TestContext.WriteLine($"records={count}; branches={branches}; archiveBytes={archiveSize}; initializeMs={timer.Elapsed.TotalMilliseconds:F2}");
            for (var pass = 0; pass < 3; pass++)
            {
                timer.Restart();
                var stages = new List<string>();
                var snapshot = await new HistoryPresentationQueryService(runtime, (stage, time) => stages.Add($"{stage}={time.TotalMilliseconds:F2}ms")).QueryAsync(sourceId);
                Assert.HasCount(count, snapshot.Timeline);
                Assert.HasCount(branches, snapshot.Branches);
                Assert.IsTrue(snapshot.Timeline.All(item => item.Readiness == HistoryPresentationReadiness.Ready));
                TestContext.WriteLine($"pass={pass}; totalMs={timer.Elapsed.TotalMilliseconds:F2}; {string.Join("; ", stages)}");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
