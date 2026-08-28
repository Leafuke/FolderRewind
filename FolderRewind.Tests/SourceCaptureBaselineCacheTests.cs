using FolderRewind.History.Capture;
using FolderRewind.History.Domain;

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
}
