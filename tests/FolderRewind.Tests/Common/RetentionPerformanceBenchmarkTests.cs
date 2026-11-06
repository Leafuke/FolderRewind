using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Services;

namespace FolderRewind.Tests;

// Real services and archives, exclusively in a newly-created fixture. Ordinary test runs skip this workload.
[TestClass, DoNotParallelize]
public sealed class RetentionPerformanceBenchmarkTests
{
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasureChainRewriteStagesAndRepeatedVerification()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_RETENTION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) Assert.Inconclusive("Set FOLDERREWIND_RETENTION_OUTPUT to opt into isolated retention measurement.");
        Assert.IsFalse(Path.Exists(output), "Measurement output must be new.");
        Directory.CreateDirectory(output);
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_RETENTION_REPEATS"), out var count) ? count : 3;
        Assert.IsTrue(repeats is > 0 and <= 10);
        var depths = (Environment.GetEnvironmentVariable("FOLDERREWIND_RETENTION_DEPTHS") ?? "3,6,12")
            .Split(',').Select(int.Parse).ToArray();
        Assert.IsTrue(depths.All(depth => depth is >= 3 and <= 24));
        var rows = new List<object>();
        foreach (var depth in depths)
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            await using var fixture = new HistoryChainRewriteFixture();
            await fixture.InitializeAsync();
            var root = await fixture.AddAsync(null, directory =>
            {
                Directory.CreateDirectory(Path.Combine(directory, "empty"));
                for (var index = 0; index < 32; index++) Write(directory, index, 0);
                File.SetAttributes(Path.Combine(directory, "f031.bin"), FileAttributes.Hidden);
            });
            var tip = root;
            for (var layer = 1; layer < depth; layer++)
            {
                var changedLayer = layer;
                tip = await fixture.AddAsync(tip, directory => Write(directory, changedLayer % 16, changedLayer));
            }
            var backend = new MeasuredArchiveBackend(fixture.Archive, fixture.Root);
            var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
            var planner = new HistoryChainRewritePlanner(fixture.History, engine);
            var executor = new HistoryChainRewriteExecutor(fixture.History, engine, backend);
            var retained = fixture.Nodes.Where(node => node != fixture.Nodes[1]).ToArray();
            var request = fixture.Delete(fixture.Nodes[1]) with
            {
                Origin = HistoryChainRewriteOrigin.Retention,
                RetainedVersionIds = [.. retained.Select(node => node.Version.VersionId)],
                BenefitPolicy = HistoryRetentionBenefitPolicy.CountFirst,
                MaximumDeltaDepth = 0
            };
            // CountFirst isolates one rewrite attempt. MaximumDeltaDepth=0 preserves the
            // tested chain length instead of inserting unrelated periodic full checkpoints.
            var work = HistoryRewriteStoragePaths.ManagedRoot(fixture.BackupRoot, fixture.Config);
            var plan = await Measure("planner", () => planner.PlanAsync(request));
            Assert.IsTrue(plan.CanExecute, string.Join(" ", plan.Blockers));
            await using var prepared = await Measure("prepare", () => executor.PrepareAsync(plan));
            Assert.IsTrue(prepared.CanCommit, prepared.Diagnostic);
            var result = await Measure("commit", () => executor.CommitAsync(prepared));
            Assert.IsTrue(result.Committed, result.Diagnostic);
            Assert.IsFalse(result.CleanupPending, result.Diagnostic);
            Assert.IsFalse(File.Exists(fixture.Nodes[1].Entry.Locator.AbsolutePath));
            // The fixture compares digest, length, last-write timestamp, file attributes,
            // and complete directory inventory. Keep correctness reads outside timings.
            await fixture.AssertRestoresAsync(retained);
            await fixture.RestartAsync();
            await fixture.AssertRestoresAsync(retained);
            rows.Add(new { depth, repeat, stage = "correctness", exactBeforeAndAfterRestart = true,
                retainedVersions = retained.Length, result.RewrittenVersions, result.CreatedBytes, result.ReclaimedBytes });
            await Save();

            async Task<T> Measure<T>(string stage, Func<Task<T>> action)
            {
                backend.Stage = stage;
                var firstCall = backend.Calls.Count;
                await using var sampler = new WorkspaceSampler(work);
                var allocation = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                var diagnostic = HistoryOperationDiagnostics.Begin("retention-" + stage);
                T value;
                using (diagnostic) value = await action();
                watch.Stop();
                await sampler.StopAsync();
                var calls = backend.Calls.Skip(firstCall).ToArray();
                var repeatedMs = calls.Where(call => call.RepeatedWithinStage).Sum(call => call.ElapsedMs);
                rows.Add(new
                {
                    depth, repeat, stage, elapsedMs = watch.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocation,
                    diagnostic = diagnostic.Summary, deepVerificationCalls = calls,
                    repeatedDeepVerificationMs = repeatedMs,
                    repeatedDeepVerificationFraction = repeatedMs / watch.Elapsed.TotalMilliseconds,
                    repeatedVerificationAtLeast20Percent = repeatedMs >= watch.Elapsed.TotalMilliseconds * 0.2,
                    // These totals are nested in the stage, not added to its duration.
                    deepVerificationMs = calls.Sum(call => call.ElapsedMs),
                    repeatedAcrossOperationMs = calls.Where(call => call.RepeatedWithinOperation).Sum(call => call.ElapsedMs),
                    workspace = sampler.Snapshot
                });
                await Save();
                return value;
            }
        }

        Task Save() => File.WriteAllTextAsync(Path.Combine(output, "retention-stages.json"), JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(), cpuCount = Environment.ProcessorCount, cache = "uncontrolled",
            dataset = "32 deterministic 4KiB files, one changed file per delta, one empty directory, one hidden file; delete layer 1",
            threshold = "Repeated successful VerifyAsync calls for the same representation and path within the stage / stage wall time. This is a measured opportunity, not proof every repeated check can safely be removed.",
            measurement = "Existing diagnostics include 7z t/l/x phases and counters; hash counters need the same observability patch on baseline and candidate. Workspace metadata sampled each 100ms plus stage boundaries; peak is an observed lower bound, sampling allocations/time are included.",
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Write(string directory, int index, int layer)
    {
        var data = new byte[4096];
        new Random(719 + index * 31 + layer * 997).NextBytes(data);
        var path = Path.Combine(directory, $"f{index:D3}.bin");
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(layer));
    }

    private sealed record VerificationCall(string Stage, string Representation, string Path, double ElapsedMs,
        bool Success, bool RepeatedWithinStage, bool RepeatedWithinOperation);

    // Preserve the backend's preflight interface so a future context-reuse candidate
    // exercises the real optimized path instead of silently falling back in this probe.
    private sealed class MeasuredArchiveBackend(SevenZipArchiveProcessBackend inner, string root)
        : IArchiveRepresentationBackend, IHistoryChainRewriteArchiveBackend, IArchiveRestorePreflightBackend
    {
        private readonly HashSet<(string Stage, RepresentationId Id, string Path)> _stageSuccess = [];
        private readonly HashSet<(RepresentationId Id, string Path)> _operationSuccess = [];
        internal string Stage { get; set; } = "";
        internal List<VerificationCall> Calls { get; } = [];
        public object InputPolicyIdentity => inner.InputPolicyIdentity;
        public bool TryGetListing(string path, out ArchiveSafeListing? listing) => inner.TryGetListing(path, out listing);
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
            => Verify(representation, path, () => inner.VerifyAsync(representation, path, token));
        public ValueTask<PayloadVerificationResult> DeepVerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
            => VerifyAsync(representation, path, token);
        public ValueTask<PayloadVerificationResult> PreflightAsync(VersionRepresentation representation, string path, bool fullContentTest, CancellationToken token)
            => fullContentTest ? Verify(representation, path, () => inner.PreflightAsync(representation, path, true, token))
                : inner.PreflightAsync(representation, path, false, token);
        private async ValueTask<PayloadVerificationResult> Verify(VersionRepresentation representation, string path, Func<ValueTask<PayloadVerificationResult>> action)
        {
            var key = Path.GetFullPath(path).ToUpperInvariant();
            var watch = Stopwatch.StartNew();
            var result = await action();
            watch.Stop();
            var repeatedStage = _stageSuccess.Contains((Stage, representation.RepresentationId, key));
            var repeatedOperation = _operationSuccess.Contains((representation.RepresentationId, key));
            Calls.Add(new(Stage, representation.RepresentationId.ToString(), Path.GetRelativePath(root, path), watch.Elapsed.TotalMilliseconds,
                result.Success, repeatedStage, repeatedOperation));
            if (result.Success)
            {
                _stageSuccess.Add((Stage, representation.RepresentationId, key));
                _operationSuccess.Add((representation.RepresentationId, key));
            }
            return result;
        }
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
            => inner.MaterializeAsync(inputs, staging, token);
        public ValueTask MaterializeRestoreAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging,
            ArchiveRestoreVerificationContext context, CancellationToken token) => inner.MaterializeRestoreAsync(inputs, staging, context, token);
        public long EstimateMaterializationBytes(IReadOnlyList<ArchiveMaterializationInput> inputs, ArchiveRestoreVerificationContext context)
            => inner.EstimateMaterializationBytes(inputs, context);
        public Task<HistoryCompactionPayload> CreateFullAsync(SourceVersion version, string directory, RepresentationId id, string output, CancellationToken token)
            => inner.CreateFullAsync(version, directory, id, output, token);
        public Task<HistoryCompactionPayload> CreateDeltaAsync(SourceVersion version, string directory, ImmutableArray<string> changed,
            RepresentationId id, string output, CancellationToken token) => inner.CreateDeltaAsync(version, directory, changed, id, output, token);
    }

    private sealed class WorkspaceSampler : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private long _peakWork, _peakTotal, _samples;
        private double _scanMs;
        private bool _stopped;
        internal object Snapshot => new { peakWorkBytes = _peakWork, peakOperationBytes = _peakTotal, samples = _samples, metadataScanMs = _scanMs, intervalMs = 100 };
        internal WorkspaceSampler(string root)
        {
            _root = root;
            Observe();
            _loop = Task.Run(async () =>
            {
                try { while (true) { await Task.Delay(100, _stop.Token); Observe(); } }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }
        private void Observe()
        {
            var watch = Stopwatch.StartNew();
            long total = 0, work = 0;
            try
            {
                if (Directory.Exists(_root))
                    foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            total += file.Length;
                            if (Path.GetRelativePath(_root, file.FullName).Split(Path.DirectorySeparatorChar).Contains("work")) work += file.Length;
                        }
                        catch (IOException) { }
                    }
            }
            catch (IOException) { }
            _peakWork = Math.Max(_peakWork, work); _peakTotal = Math.Max(_peakTotal, total);
            _samples++; _scanMs += watch.Elapsed.TotalMilliseconds;
        }
        internal async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            _stop.Cancel(); await _loop;
            Observe();
        }
        public async ValueTask DisposeAsync() { await StopAsync(); _stop.Dispose(); }
    }
}
