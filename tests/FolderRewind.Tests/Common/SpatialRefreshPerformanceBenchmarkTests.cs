using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System.Diagnostics;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SpatialRefreshPerformanceBenchmarkTests
{
    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task MeasureSpatialScanRefreshStrategies()
    {
        var outputParent = Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputParent)) Assert.Inconclusive("Set FOLDERREWIND_SPATIAL_OUTPUT for isolated opt-in measurements.");
        var sizes = (Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_SIZES") ?? "10000,50000,100000")
            .Split(',').Select(int.Parse).ToArray();
        if (sizes.Length == 0 || sizes.Any(size => size is < 500 or > 100000)) throw new ArgumentOutOfRangeException(nameof(sizes));
        var repeats = int.Parse(Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_REPEATS") ?? "1");
        if (repeats is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(repeats));
        var output = Path.Combine(Path.GetFullPath(outputParent), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var measurements = new List<Measurement>();
        var validations = new List<object>();
        var json = new JsonSerializerOptions { WriteIndented = true };

        foreach (var size in sizes)
        {
            // Identical deterministic typed inputs, generated outside both measured intervals.
            var input = Enumerable.Range(0, size).Select(CreateRow).ToArray();
            for (var repeat = 0; repeat < repeats; repeat++)
            {
                var directory = Path.Combine(output, $"rows-{size}-repeat-{repeat}");
                Directory.CreateDirectory(directory);
                using var baseline = new SpatialWorkspaceIndex(Path.Combine(directory, "baseline.db"));
                using var candidate = new SpatialWorkspaceIndex(Path.Combine(directory, "coalesced.db"));
                baseline.Put([]); candidate.Put([]); // Schema creation is not scan/refresh work.
                var query = new SpatialWorkspaceQuery("world-a", "layer", Categories: ["blocks", "entities", "regions"],
                    KnownCategories: ["blocks", "entities", "regions"]);
                var order = (Array.IndexOf(sizes, size) + repeat) % 2 == 0
                    ? new[] { "per-page", "coalesced" } : new[] { "coalesced", "per-page" };
                var finalRefreshes = new Dictionary<string, SpatialWorkspacePresentation>();
                foreach (var strategy in order)
                {
                    var measured = await MeasureAsync(strategy == "per-page" ? baseline : candidate,
                        input, query, strategy, repeat);
                    measurements.Add(measured.Measurement);
                    finalRefreshes.Add(strategy, measured.FinalPresentation);
                    Save();
                }

                var verify = Stopwatch.StartNew();
                // Full typed input/output comparison is outside the performance measurements.
                Assert.AreEqual(size, finalRefreshes["per-page"].Global.Total);
                Assert.AreEqual(size, finalRefreshes["coalesced"].Global.Total);
                AssertPresentationEqual(finalRefreshes["per-page"], finalRefreshes["coalesced"]);
                var baselineRows = baseline.Query("", "", 0, int.MaxValue);
                var candidateRows = candidate.Query("", "", 0, int.MaxValue);
                Assert.HasCount(size, baselineRows);
                AssertRowsEqual(input.OrderBy(row => row.Title, StringComparer.Ordinal)
                    .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray(), baselineRows);
                AssertRowsEqual(baselineRows, candidateRows);
                var views = new[] { query, query with { World = "world-b" }, query with { Files = true },
                    query with { Categories = ["blocks"] }, query with { Search = "Cell 000" } };
                var groupedRows = new List<int>();
                foreach (var view in views)
                {
                    AssertPresentationEqual(ReadPerPage(baseline, view), candidate.ReadPresentation(view, 0, false));
                    var left = baseline.Query(view, 0, int.MaxValue);
                    var right = candidate.Query(view, 0, int.MaxValue);
                    AssertRowsEqual(left, right);
                    groupedRows.Add(left.Count);
                }
                var representative = candidate.ReadPresentation(query, 0, false);
                Assert.IsGreaterThan(0, representative.Total);
                Assert.IsGreaterThan(0, representative.WholeDomain);
                Assert.Contains("blocks", representative.Categories.Keys);
                Assert.Contains("entities", representative.Categories.Keys);
                validations.Add(new { size, repeat, rawRows = size, views = views.Length, groupedRows,
                    elapsedMs = verify.Elapsed.TotalMilliseconds, correct = true });
                Save();
                Console.WriteLine($"Spatial rows={size}, repeat={repeat}: typed outputs match; results={Path.Combine(output, "results.json")}");
            }
        }

        void Save() => File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            runtime = Environment.Version.ToString(),
            sqliteAssembly = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.FullName,
            sizes, repeats, batchSize = 500, intervalMilliseconds = 250,
            scope = "Scheduling strategy comparison using the same current production SpatialWorkspaceIndex SQL; not old/new binary A/B, UI frame timing, plugin scanning, or remote I/O",
            workload = "Deterministic co-located blocks/entities, multi-component rows, two worlds, whole-domain and non-spatial rows; schema/input generation and full typed verification outside timings",
            presentation = "Catalog (merge=false): original seven-field tuple includes six read entry points and an empty merge-count slot; the category-filtered view also reads the unfiltered total",
            cache = "OS cache uncontrolled; variant order alternates by size/repeat; both private index connections stay open; no forced GC within/between measured variants",
            allocation = "Process-wide managed allocation delta including producer, coordinator, async queries and benchmark counters; not retained heap",
            measurements, validations
        }, json));
    }

    private static async Task<MeasuredStrategy> MeasureAsync(SpatialWorkspaceIndex index, SpatialWorkspaceRow[] input,
        SpatialWorkspaceQuery query, string strategy, int repeat)
    {
        int attempted = 0, completed = 0, canceled = 0, running = 0, peak = 0;
        SpatialWorkspacePresentation? finalPresentation = null;
        var refreshTimes = new List<double>(); // Only one reader writes; producer does not touch it.
        async Task Refresh(SpatialWorkspaceQuery request, CancellationToken token)
        {
            Interlocked.Increment(ref attempted);
            var active = Interlocked.Increment(ref running);
            peak = Math.Max(peak, active);
            var clock = Stopwatch.StartNew();
            try
            {
                var presentation = await Task.Run(() => index.ReadPresentation(request, 0, false, token), token);
                token.ThrowIfCancellationRequested();
                finalPresentation = presentation;
                Interlocked.Increment(ref completed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { Interlocked.Increment(ref canceled); throw; }
            finally { refreshTimes.Add(clock.Elapsed.TotalMilliseconds); Interlocked.Decrement(ref running); }
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var total = Stopwatch.StartNew();
        double ingestMs;
        if (strategy == "per-page")
        {
            foreach (var batch in input.Chunk(500))
            {
                index.Put(batch, allowReplace: false);
                var clock = Stopwatch.StartNew();
                attempted++; running++; peak = Math.Max(peak, running);
                finalPresentation = ReadPerPage(index, query);
                running--; completed++;
                refreshTimes.Add(clock.Elapsed.TotalMilliseconds);
            }
            ingestMs = total.Elapsed.TotalMilliseconds;
        }
        else
        {
            await using var coordinator = new SpatialPresentationRefreshCoordinator<SpatialWorkspaceQuery>(Refresh,
                TimeSpan.FromMilliseconds(250));
            var requests = new List<Task>();
            var first = true;
            foreach (var batch in input.Chunk(500))
            {
                index.Put(batch, allowReplace: false);
                requests.Add(coordinator.RequestAsync(query, immediate: first));
                first = false;
            }
            ingestMs = total.Elapsed.TotalMilliseconds;
            requests.Add(coordinator.RequestAsync(query, immediate: true));
            await Task.WhenAll(requests);
            await coordinator.DrainAsync();
            // Disposal is included; no useful read remains after the measured operation.
        }
        total.Stop();
        process.Refresh();
        Assert.AreEqual(0, running);
        Assert.IsLessThanOrEqualTo(1, peak);
        var measurement = new Measurement(strategy, input.Length, repeat, ingestMs, total.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collections[generation]).ToArray(),
            attempted, completed, canceled, peak, refreshTimes.ToArray());
        return new(measurement, finalPresentation ?? throw new InvalidOperationException("No final refresh completed."));
    }

    // Original VM tuple: Query, Count, Counts, CategoryCounts, WholeDomainCount,
    // unfiltered Count and (for compare) the empty MergeCounts slot. No new snapshot shortcut.
    private static SpatialWorkspacePresentation ReadPerPage(SpatialWorkspaceIndex index, SpatialWorkspaceQuery query)
        => new(index.Query(query, 0, 101), index.Count(query), index.Counts(), index.CategoryCounts(query),
            index.WholeDomainCount(query), index.Count(query with { Categories = null }), []);

    private static SpatialWorkspaceRow CreateRow(int number)
    {
        var position = number / 2;
        var world = position % 3 == 0 ? "world-b" : "world-a";
        var location = number % 211 == 0 ? Array.Empty<SpatialSubjectLocation>()
            : new[] { new SpatialSubjectLocation(world, number % 997 == 0 ? null : "layer",
                number % 997 == 0 ? null : new((position % 1000) * 16d, (position / 1000) * 16d,
                    (position % 1000 + 1) * 16d, (position / 1000 + 1) * 16d)) };
        var component = number % 2 == 0 ? "blocks" : "entities";
        return new("row-" + number.ToString("D6"), "Cell " + position.ToString("D6"), "Deterministic row " + number,
            number % 7 == 0 ? "Added" : "Modified", false, location)
        {
            Components = number % 11 == 0 ? [component, "regions"] : [component],
            SourcePaths = ["region/r." + (position % 1000) + "." + (position / 1000) + ".mca"]
        };
    }

    private static void AssertPresentationEqual(SpatialWorkspacePresentation expected, SpatialWorkspacePresentation actual)
    {
        Assert.AreEqual(expected.Total, actual.Total); Assert.AreEqual(expected.Global, actual.Global);
        Assert.AreEqual(expected.WholeDomain, actual.WholeDomain); Assert.AreEqual(expected.UnfilteredTotal, actual.UnfilteredTotal);
        CollectionAssert.AreEqual(expected.MergeCounts, actual.MergeCounts);
        CollectionAssert.AreEqual(expected.Categories.Keys.Order(StringComparer.Ordinal).ToArray(), actual.Categories.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach (var key in expected.Categories.Keys) Assert.AreEqual(expected.Categories[key], actual.Categories[key]);
        AssertRowsEqual(expected.Rows, actual.Rows);
    }

    private static void AssertRowsEqual(IReadOnlyList<SpatialWorkspaceRow> expected, IReadOnlyList<SpatialWorkspaceRow> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index]; var right = actual[index];
            Assert.AreEqual(left.Id, right.Id); Assert.AreEqual(left.Title, right.Title); Assert.AreEqual(left.Description, right.Description);
            Assert.AreEqual(left.Status, right.Status); Assert.AreEqual(left.CanSelect, right.CanSelect); Assert.AreEqual(left.Checked, right.Checked);
            CollectionAssert.AreEqual(left.Locations.ToArray(), right.Locations.ToArray());
            CollectionAssert.AreEqual(left.Components, right.Components); CollectionAssert.AreEqual(left.SourcePaths, right.SourcePaths);
            CollectionAssert.AreEqual(left.MemberIds, right.MemberIds); CollectionAssert.AreEqual(left.States, right.States);
        }
    }

    private sealed record Measurement(string Strategy, int Rows, int Repeat, double IngestMs, double TotalMs, double CpuMs,
        long AllocatedBytes, int[] GcCollections, int AttemptedRefreshes, int CompletedRefreshes, int CanceledRefreshes,
        int PeakConcurrentReads, double[] RefreshElapsedMs);

    private sealed record MeasuredStrategy(Measurement Measurement, SpatialWorkspacePresentation FinalPresentation);
}
