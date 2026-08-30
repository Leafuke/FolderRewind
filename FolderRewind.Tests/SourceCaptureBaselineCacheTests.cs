using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SourceCaptureBaselineCacheTests
{
    [TestMethod]
    public async Task CorruptCacheIsDiscardedSoCaptureCanFallBackToFull()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FolderRewindBaselineCacheTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var sourceId = new SourceId(Guid.NewGuid());
            var cacheDirectory = Path.Combine(root, "capture-baselines");
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllTextAsync(Path.Combine(cacheDirectory, sourceId + ".json"), "{}");
            using var cache = new SourceCaptureBaselineCache(root);

            Assert.IsNull(await cache.LoadAsync(sourceId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoveUsesRevisionCompareAndSwap()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FolderRewindBaselineCacheTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var sourceId = SourceId.New();
            var versionId = VersionId.New();
            var representation = new VersionRepresentation(
                RepresentationId.New(), versionId, RepresentationKind.CoreFull, "7z", [],
                MaterializationFidelity.Exact, null, null, null);
            using var cache = new SourceCaptureBaselineCache(root);
            await cache.SaveAsync(
                sourceId,
                new SourceCaptureBaselineCandidate(
                    SourceCaptureBaselineCache.MissingRevision,
                    Path.Combine(root, "payload.7z"),
                    0,
                    ImmutableSortedDictionary<string, SourceCaptureFileState>.Empty),
                versionId,
                representation);

            Assert.IsFalse(await cache.RemoveAsync(sourceId, expectedRevision: 99));
            Assert.IsNotNull(await cache.LoadAsync(sourceId));
            Assert.IsTrue(await cache.RemoveAsync(sourceId, expectedRevision: 0));
            Assert.IsNull(await cache.LoadAsync(sourceId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ApplicabilityRequiresExactMatchingWorkspaceVersion()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var sourceId = SourceId.New();
        var versionId = VersionId.New();
        var baseline = new SourceCaptureBaseline(
            sourceId,
            0,
            versionId,
            RepresentationId.New(),
            RepresentationKind.CoreFull,
            Path.GetFullPath("payload.7z"),
            0,
            ImmutableSortedDictionary<string, SourceCaptureFileState>.Empty);
        var exact = new HistoryWorkspace(
            configId, 0, null, null,
            [new WorkspaceSourceBaseline(sourceId, versionId, WorkspaceBaselineRelation.Exact)]);
        var differentVersion = new HistoryWorkspace(
            configId, 0, null, null,
            [new WorkspaceSourceBaseline(sourceId, VersionId.New(), WorkspaceBaselineRelation.Exact)]);
        var derived = new HistoryWorkspace(
            configId, 0, null, null,
            [new WorkspaceSourceBaseline(sourceId, versionId, WorkspaceBaselineRelation.Derived)]);

        Assert.IsTrue(SourceCaptureBaselinePolicy.IsApplicableToWorkspace(baseline, exact));
        Assert.IsFalse(SourceCaptureBaselinePolicy.IsApplicableToWorkspace(baseline, differentVersion));
        Assert.IsFalse(SourceCaptureBaselinePolicy.IsApplicableToWorkspace(baseline, derived));
        Assert.IsFalse(SourceCaptureBaselinePolicy.IsApplicableToWorkspace(baseline, null));
    }
}
