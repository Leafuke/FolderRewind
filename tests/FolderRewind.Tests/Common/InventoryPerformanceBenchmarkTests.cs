using System.Diagnostics;
using System.Text.Json;
using FolderRewind.History.Merge;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass, DoNotParallelize]
public sealed class InventoryPerformanceBenchmarkTests
{
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasureMergeDirectoryTraversalWithinTheWholeRead()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_INVENTORY_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) Assert.Inconclusive("Set an isolated inventory output to opt in.");
        Assert.IsFalse(Path.Exists(output), "Output must be new.");
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        foreach (var count in new[] { 1000, 20000 })
        {
            var root = Path.Combine(output, count.ToString(), "source");
            for (var i = 0; i < count; i++)
            {
                var directory = Path.Combine(root, $"d{i / 100:D4}", "inner");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, $"f{i:D6}.txt"), $"{i:D6}:deterministic");
            }
            Directory.CreateDirectory(Path.Combine(root, "empty", "nested"));
            var expected = await MergeTreeManifest.ReadAsync(root, _ => true, default);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var diagnostic = HistoryOperationDiagnostics.Begin("merge-inventory-benchmark");
                var before = GC.GetTotalAllocatedBytes(true);
                var clock = Stopwatch.StartNew();
                MergeTreeManifest result;
                using (diagnostic) result = await MergeTreeManifest.ReadAsync(root, _ => true, default);
                clock.Stop();
                Assert.AreEqual(expected.Digest, result.Digest);
                Assert.HasCount(count, result.Files);
                Assert.Contains("empty/nested", result.Directories);
                rows.Add(new { count, repeat, elapsedMs = clock.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - before, diagnostic = diagnostic.Summary });
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
                {
                    scope = "Production MergeTreeManifest read of isolated immutable tiny files, one warmup and three measurements; directory-inventory is an inclusive component of the whole read, not a second algorithm A/B. OS cache is uncontrolled.",
                    rows
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
