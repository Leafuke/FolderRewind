using System.Diagnostics;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

public sealed partial class NativeArtifactStoreTests
{
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasureNativeCaptureConcurrencyAndFaultCleanup()
    {
        var parent = Environment.GetEnvironmentVariable("FOLDERREWIND_RUNTIME_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(parent)) Assert.Inconclusive("Explicit isolated Runtime benchmark opt-in required.");
        var output = Path.Combine(parent, "native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_RUNTIME_BENCHMARK_REPEATS"), out var n) ? n : 3;
        Assert.IsTrue(repeats is > 0 and <= 10);
        foreach (var outer in new[] { 1, 4, 8 }) foreach (var capacity in new[] { 1, 4 })
        foreach (var shared in new[] { false, true }) for (var repeat = 0; repeat < repeats; repeat++)
        {
            var fixtures = Enumerable.Range(0, outer).Select(_ => new Fixture()).ToArray();
            var sharedRoot = Path.Combine(Path.GetTempPath(), "NativePressureShared-" + Guid.NewGuid().ToString("N"));
            var codec = new ZipCodec(capacity);
            int activeOuter = 0, peakOuter = 0, peakLeases = 0;
            var allocation = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            try
            {
                var captures = fixtures.Select(fixture => Task.Run(async () =>
                {
                    Maximum(ref peakOuter, Interlocked.Increment(ref activeOuter));
                    try
                    {
                        return await fixture.CaptureAsync(diamond: true, suppliedCodec: codec, validatePrepared: true,
                            storageRoot: shared ? sharedRoot : null,
                            observe: _ => Maximum(ref peakLeases, fixtures.Sum(f => f.Runtime?.GetSnapshot(Id).ActiveLeases ?? 0)));
                    }
                    finally { Interlocked.Decrement(ref activeOuter); }
                })).ToArray();
                var results = await Task.WhenAll(captures);
                watch.Stop();
                var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation;
                Assert.AreEqual(0, codec.Workers);
                Assert.IsTrue(fixtures.All(f => f.Runtime!.GetSnapshot(Id).ActiveLeases == 0));
                Assert.IsLessThanOrEqualTo(shared ? capacity : outer * capacity, codec.PeakWorkers);
                foreach (var store in results.Select(result => result.Store).DistinctBy(store => store.Root))
                {
                    Assert.IsEmpty(store.Pending());
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var available = await store.EnterAsync(timeout.Token);
                }
                rows.Add(new { scenario = "capture", outer, codecCapacity = capacity, sharedStore = shared, repeat,
                    elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes,
                    peakOuter, peakPluginLeasesObserved = peakLeases, codecPeakWorkers = codec.PeakWorkers,
                    codecPackCalls = codec.Packs, codecUnpackCalls = codec.Unpacks, finalCodecWorkers = codec.Workers,
                    finalPluginLeases = fixtures.Sum(f => f.Runtime!.GetSnapshot(Id).ActiveLeases), correct = true });
                await Save();
            }
            finally
            {
                foreach (var fixture in fixtures)
                {
                    if (fixture.Runtime is { } runtime) await runtime.DeactivateAsync(Id);
                    fixture.Dispose();
                }
                if (Directory.Exists(sharedRoot)) Directory.Delete(sharedRoot, true);
            }
        }
        foreach (var cancel in new[] { false, true })
        {
            using var fixture = new Fixture();
            var codec = new ZipCodec(4);
            var (store, root) = await fixture.CaptureAsync(diamond: true, suppliedCodec: codec);
            using var cancellation = new CancellationTokenSource();
            codec.AfterUnpack = directory =>
            {
                if (cancel) cancellation.Cancel();
                else
                {
                    var file = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).First();
                    var bytes = File.ReadAllBytes(file); bytes[0] ^= 1; File.WriteAllBytes(file, bytes);
                }
            };
            var watch = Stopwatch.StartNew();
            try
            {
                if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => store.OpenAsync(root, cancellation.Token));
                else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenAsync(root, default));
                var failureMs = watch.Elapsed.TotalMilliseconds;
                Assert.AreEqual(0, codec.Workers);
                Assert.IsEmpty(Directory.GetDirectories(Path.Combine(store.Root, ".read")));
                codec.AfterUnpack = null;
                watch.Restart();
                await using (var retry = await store.OpenAsync(root, default))
                {
                    Assert.HasCount(4, retry.Artifacts);
                    foreach (var artifact in retry.Artifacts) Assert.IsNotEmpty(await retry.Read.ListFilesAsync(artifact.Content, default));
                }
                watch.Stop();
                Assert.AreEqual(0, codec.Workers);
                using var released = await store.EnterAsync(default);
                rows.Add(new { scenario = cancel ? "decode-cancellation" : "logical-mismatch", failureMs,
                    retryMs = watch.Elapsed.TotalMilliseconds, finalCodecWorkers = codec.Workers,
                    finalPluginLeases = fixture.Runtime!.GetSnapshot(Id).ActiveLeases, correct = true });
                await Save();
            }
            finally { codec.AfterUnpack = null; await fixture.Runtime!.DeactivateAsync(Id); }
        }

        Task Save() => File.WriteAllTextAsync(Path.Combine(output, "native-resource.json"), JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(), cpuCount = Environment.ProcessorCount,
            scope = "Production NativeArtifactStore with existing synthetic graph fixture and controlled ZipCodec. Capacity=1/4 is codec test capacity, not actual 7z thread/CPU policy. Capacity>1 retains existing fixture's 20ms artificial work delay. Outer count includes queued captures; plugin-lease peak is observed at prepared callbacks, not a continuous exact maximum.", rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Maximum(ref int target, int value)
    {
        int before;
        do { before = Volatile.Read(ref target); if (before >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, before) != before);
    }
}

public sealed partial class PluginRuntimeManagerTests
{
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasureDrainCooperationCancellationAndCrossPluginGate()
    {
        var parent = Environment.GetEnvironmentVariable("FOLDERREWIND_RUNTIME_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(parent)) Assert.Inconclusive("Explicit isolated Runtime benchmark opt-in required.");
        var output = Path.Combine(parent, "drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_RUNTIME_BENCHMARK_REPEATS"), out var n) ? n : 3;
        Assert.IsTrue(repeats is > 0 and <= 10);
        foreach (var cooperative in new[] { true, false }) for (var repeat = 0; repeat < repeats; repeat++)
        {
            var manager = new PluginRuntimeManager();
            Assert.IsTrue((await manager.ActivateAsync(Candidate(PluginOneId, PluginWithDiscovery("one"), new FakeStore()))).Success);
            Assert.IsTrue((await manager.ActivateAsync(Candidate(PluginTwoId, OtherPlugin(), new FakeStore()))).Success);
            using var operationCancellation = new CancellationTokenSource();
            using var drainCancellation = new CancellationTokenSource();
            var stopWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lease = manager.TryAcquireOperation(PluginOneId, operationCancellation.Token)!;
            var worker = Task.Run(async () =>
            {
                try
                {
                    if (cooperative) await Task.Delay(Timeout.InfiniteTimeSpan, lease.Context.OperationCancellation);
                    else await stopWorker.Task;
                }
                catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested) { }
                finally { lease.Dispose(); } // Only the worker releases resources after it has stopped using them.
            });
            var clock = Stopwatch.StartNew();
            var drain = manager.DeactivateAsync(PluginOneId, drainCancellation.Token).AsTask();
            Task<PluginRuntimeTransitionResult>? other = null;
            try
            {
                await WaitForStateAsync(manager, PluginOneId, PluginRuntimeState.Draining);
                using (var rejected = manager.TryAcquireOperation(PluginOneId)) Assert.IsNull(rejected);
                var otherClock = Stopwatch.StartNew();
                other = manager.ReplaceAsync(Candidate(PluginTwoId, OtherPlugin(), new FakeStore())).AsTask();
                await Task.Delay(100);
                var crossPluginBlocked = !other.IsCompleted;
                Assert.IsTrue(crossPluginBlocked);
                Assert.AreEqual(1, manager.GetSnapshot(PluginOneId).ActiveLeases);
                operationCancellation.Cancel();
                double? canceledDrainMs = null;
                var pendingAfterOperationCancellation = false;
                if (!cooperative)
                {
                    await Task.Delay(100);
                    pendingAfterOperationCancellation = !drain.IsCompleted && !other.IsCompleted;
                    Assert.IsTrue(pendingAfterOperationCancellation);
                    Assert.AreEqual(1, manager.GetSnapshot(PluginOneId).ActiveLeases);
                    var cancelClock = Stopwatch.StartNew();
                    drainCancellation.Cancel();
                    var canceled = await drain.WaitAsync(TimeSpan.FromSeconds(5));
                    canceledDrainMs = cancelClock.Elapsed.TotalMilliseconds;
                    Assert.AreEqual(OperationOutcome.Canceled, canceled.Outcome);
                    Assert.AreEqual(PluginRuntimeState.Active, manager.GetSnapshot(PluginOneId).State);
                    using (var recovered = manager.TryAcquireOperation(PluginOneId)) Assert.IsNotNull(recovered);
                    Assert.AreEqual(1, manager.GetSnapshot(PluginOneId).ActiveLeases);
                }
                else
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.IsTrue((await drain.WaitAsync(TimeSpan.FromSeconds(5))).Success);
                }
                Assert.IsTrue((await other.WaitAsync(TimeSpan.FromSeconds(5))).Success);
                var otherTransitionMs = otherClock.Elapsed.TotalMilliseconds;
                stopWorker.TrySetResult();
                await worker;
                if (!cooperative) Assert.IsTrue((await manager.DeactivateAsync(PluginOneId)).Success);
                clock.Stop();
                Assert.AreEqual(0, manager.GetSnapshot(PluginOneId).ActiveLeases);
                rows.Add(new { scenario = "lease-drain", cooperative, repeat, totalMs = clock.Elapsed.TotalMilliseconds,
                    otherTransitionMs, canceledDrainMs, crossPluginBlocked, pendingAfterOperationCancellation,
                    finalPluginLeases = manager.GetSnapshot(PluginOneId).ActiveLeases, correct = true });
                await Save();
            }
            finally
            {
                operationCancellation.Cancel(); stopWorker.TrySetResult();
                await worker; drainCancellation.Cancel(); await drain;
                if (other is not null) await other;
                await manager.DeactivateAsync(PluginOneId); await manager.DeactivateAsync(PluginTwoId);
            }
        }
        // Deactivation callback timeout is distinct from waiting on an active lease.
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutManager = new PluginRuntimeManager(deactivationGracePeriod: TimeSpan.FromMilliseconds(50));
        var hungPlugin = PluginWithDiscovery("late-cleanup", _ => new ValueTask(LateCleanup()));
        Assert.IsTrue((await timeoutManager.ActivateAsync(Candidate(PluginOneId, hungPlugin, new FakeStore()))).Success);
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await timeoutManager.DeactivateAsync(PluginOneId);
            var deactivateMs = watch.Elapsed.TotalMilliseconds;
            Assert.IsTrue(result.RequiresRestart);
            Assert.AreEqual(1, timeoutManager.RestartRetainedCount);
            watch.Restart();
            Assert.IsTrue((await timeoutManager.ActivateAsync(Candidate(PluginTwoId, OtherPlugin(), new FakeStore()))).Success);
            rows.Add(new { scenario = "deactivation-timeout", deactivateMs, otherActivationMs = watch.Elapsed.TotalMilliseconds,
                retainedInstances = timeoutManager.RestartRetainedCount, requiresRestart = result.RequiresRestart, correct = true });
            await Save();
        }
        finally
        {
            finishCleanup.TrySetResult(); await cleanupStopped.Task;
            await timeoutManager.DeactivateAsync(PluginTwoId);
        }
        async Task LateCleanup() { await finishCleanup.Task; cleanupStopped.TrySetResult(); }
        static FakePlugin OtherPlugin() => new(context =>
        {
            context.RegisterCapability<IDiscoveryCapability>(new FakeDiscovery("other", new DiscoveryProviderId("com.folderrewind.benchmark-other")));
            return PluginActivationResult.Empty;
        });
        Task Save() => File.WriteAllTextAsync(Path.Combine(output, "drain-pressure.json"), JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(), scope = "Production manager with synthetic leases/workers; 100ms observation windows are intentional. No lease is forcibly released while its worker is running; canceled drain resumes the old session. Timeout-retained instances remain logically retained until restart even after the synthetic cleanup later returns.", rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
