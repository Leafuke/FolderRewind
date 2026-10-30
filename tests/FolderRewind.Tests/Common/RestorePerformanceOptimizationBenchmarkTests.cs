using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

// Same measurement source is compiled against both frozen baseline and candidate.
// Opt-in only. Every mutation is inside a newly created fixture, never the supplied real world.
[TestClass, DoNotParallelize]
public sealed class RestorePerformanceOptimizationBenchmarkTests
{
    private static readonly DateTime Stamp = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasureFixedRestoreScenario()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_OUTPUT");
        var datasets = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_DATASETS");
        var scenario = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_SCENARIO");
        if (output is null || datasets is null || scenario is null) Assert.Inconclusive("Explicit isolated restore benchmark opt-in required.");
        Assert.IsFalse(Directory.Exists(output), "Output must be new."); Directory.CreateDirectory(output);
        int repeats = int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_REPEATS"), out var n) ? n : 5;
        Assert.IsTrue(repeats is > 0 and <= 20);
        var rows = new List<object>();
        if (scenario == "owner")
        {
            foreach (int count in new[] { 1000, 5000, 10000, 20000 }) foreach (bool nested in new[] { false, true })
            {
                var root = RepresentationId.New();
                ArchiveMaterializationInput[] inputs = [new(new(root, VersionId.New(), RepresentationKind.CoreFull, "7z", [],
                    MaterializationFidelity.Exact, null, null, ImmutableDictionary<string, string>.Empty), "unused"),
                    new(new(RepresentationId.New(), VersionId.New(), RepresentationKind.CoreSmartDelta, "7z", [root],
                    MaterializationFidelity.Exact, null, null, ImmutableDictionary<string, string>.Empty), "unused-delta")];
                var files = Enumerable.Range(0, count).ToDictionary(i => Name(i, nested), i => new ArchiveFileListingEntry(Name(i, nested), 256), StringComparer.OrdinalIgnoreCase);
                ArchiveSafeListing[] listings = [new(files, []), new(new Dictionary<string, ArchiveFileListingEntry> { [Name(0, nested)] = new(Name(0, nested), 256) }, [])];
                _ = NativeSmartOwnerPlanner.TryPlan(inputs, listings, ".restore-marker", out _);
                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    long before = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
                    var plan = NativeSmartOwnerPlanner.TryPlan(inputs, listings, ".restore-marker", out var fallback); clock.Stop();
                    long allocation = GC.GetTotalAllocatedBytes(true) - before;
                    Assert.IsNotNull(plan); Assert.AreEqual("", fallback); Assert.HasCount(count, plan.Files);
                    Assert.AreEqual(1, plan.Files[Name(0, nested)].ArchiveIndex);
                    Assert.IsTrue(plan.Files.Where(p => p.Key != Name(0, nested)).All(p => p.Value.ArchiveIndex == 0));
                    rows.Add(new { scenario, count, nested, repeat, elapsedMs = clock.Elapsed.TotalMilliseconds, allocatedBytes = allocation, correct = true });
                    await SaveAsync();
                }
            }
            return;
        }
        bool overwrite = scenario == "core-overwrite-5000";
        bool smart = scenario.StartsWith("core-smart-", StringComparison.Ordinal);
        bool nestedTree = scenario.EndsWith("-nested", StringComparison.Ordinal);
        int fileCount = overwrite ? 5000 : int.Parse(scenario.Split('-')[2]);
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, directory => {
            for (int i = 0; i < fileCount; i++) Write(directory, i, nestedTree, 0);
            FixDirectories(directory);
        });
        var payloads = new List<string>();
        await SharePayloadAsync(node, 0);
        if (smart) for (int layer = 1; layer <= 5; layer++)
        {
            int value = layer;
            node = await f.AddAsync(node, directory => { Write(directory, 0, nestedTree, value); FixDirectories(directory); });
            await SharePayloadAsync(node, layer);
        }
        for (int repeat = 0; repeat < repeats; repeat++)
        {
            var target = Path.Combine(f.Root, "restore-target"); Directory.CreateDirectory(target);
            if (overwrite) for (int i = 0; i < fileCount; i++) Write(target, i, nestedTree, -1);
            File.WriteAllText(Path.Combine(target, "extra.txt"), "old unmanaged extra");
            var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
            using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
            var restore = new HistoryRestoreService(f.History, f.Engine,
                async token => new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
                new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, VerificationContext = verification };
            var diagnostic = HistoryOperationDiagnostics.Begin(scenario);
            long before = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
            HistoryRestoreResult result; double ready;
            using (diagnostic)
            {
                result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target), workspace,
                    overwrite ? HistoryRestoreApplyMode.Overwrite : HistoryRestoreApplyMode.Clean);
                ready = clock.Elapsed.TotalMilliseconds;
                await f.History.DrainRestoreCleanupAsync();
            }
            clock.Stop(); long allocation = GC.GetTotalAllocatedBytes(true) - before;
            Assert.IsTrue(result.Succeeded, result.Diagnostic);
            var actual = await HistoryRewriteTree.ReadAsync(target);
            Assert.AreEqual(overwrite, actual.Files.ContainsKey("extra.txt"));
            Assert.IsTrue(node.Tree.EquivalentTo(actual with { Files = actual.Files.Remove("extra.txt") }), "Restored tree including timestamps/attributes differs.");
            rows.Add(new { scenario, count = fileCount, nested = nestedTree, repeat, fileReadyMs = ready, totalMs = clock.Elapsed.TotalMilliseconds,
                allocatedBytes = allocation, diagnostic = diagnostic.Summary, correct = true });
            await SaveAsync(); Directory.Delete(target, true);
        }
        async Task SharePayloadAsync(HistoryChainRewriteFixture.Node current, int layer)
        {
            var shared = Path.Combine(datasets, scenario); Directory.CreateDirectory(shared);
            var path = Path.Combine(shared, layer + ".7z"); var witness = path + ".tree.json";
            var tree = JsonSerializer.Serialize(current.Tree);
            if (File.Exists(path))
            {
                Assert.AreEqual(await File.ReadAllTextAsync(witness), tree, "Shared fixture tree mismatch.");
                File.Copy(path, current.Entry.Locator.AbsolutePath!, true);
            }
            else { File.Copy(current.Entry.Locator.AbsolutePath!, path); await File.WriteAllTextAsync(witness, tree); }
            payloads.Add(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            await File.WriteAllTextAsync(Path.Combine(output, "payloads.json"), JsonSerializer.Serialize(payloads));
        }
        async Task SaveAsync() => await File.WriteAllTextAsync(Path.Combine(output, "measurements.json"),
            JsonSerializer.Serialize(new { scenario, assemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(RestorePerformanceOptimizationBenchmarkTests).Assembly.Location))),
                cache = "uncontrolled", rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string Name(int index, bool nested) => nested ? $"d{index / 100:D4}/inner/f{index:D6}.txt" : $"f{index:D6}.txt";
    private static void Write(string root, int index, bool nested, int layer)
    {
        var path = Path.Combine(root, Name(index, nested)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{index:D6}:{layer:D3}:" + new string('x', 240)); File.SetLastWriteTimeUtc(path, Stamp.AddSeconds(layer + 1));
    }
    private static void FixDirectories(string root)
    { foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(directory, Stamp); }
}
