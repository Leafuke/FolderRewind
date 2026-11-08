using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services;

namespace FolderRewind.Tests;

public sealed partial class MineRewindBackupSizeIntegrationTests
{
    // Local opt-in acceptance. Inputs must already be isolated snapshots; never discovers user data.
    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task ExactPackageRealSnapshotCaptureAndRestore()
    {
        var input = Environment.GetEnvironmentVariable("FOLDERREWIND_PHASE3_INPUT");
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_PHASE3_OUTPUT");
        var sevenZip = Environment.GetEnvironmentVariable("FOLDERREWIND_PHASE3_7ZIP");
        if (input is null || output is null || sevenZip is null) Assert.Inconclusive("Explicit isolated input/output/7-Zip paths are required.");
        Assert.IsFalse(Directory.Exists(output), "Each measurement owns a new directory.");
        Assert.IsFalse(Path.GetFullPath(output).StartsWith(Path.GetFullPath(input) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(output);
        var sourcePath = Path.Combine(output, "source"); Directory.CreateDirectory(sourcePath);
        var excluded = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(input, "*", SearchOption.AllDirectories))
        {
            Assert.AreEqual(0, (int)(File.GetAttributes(directory) & FileAttributes.ReparsePoint));
            var relative = Path.GetRelativePath(input, directory);
            if (!relative.Split(Path.DirectorySeparatorChar).Contains("voxy", StringComparer.OrdinalIgnoreCase))
                Directory.CreateDirectory(Path.Combine(sourcePath, relative));
        }
        foreach (var path in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
        {
            Assert.AreEqual(0, (int)(File.GetAttributes(path) & FileAttributes.ReparsePoint));
            var relative = Path.GetRelativePath(input, path);
            if (Path.GetFileName(path).Equals("session.lock", StringComparison.OrdinalIgnoreCase)
                || relative.Split(Path.DirectorySeparatorChar).Contains("voxy", StringComparer.OrdinalIgnoreCase)) { excluded.Add(relative); continue; }
            File.Copy(path, Path.Combine(sourcePath, relative));
        }
        var original = Phase3Manifest(input);
        var expected = Phase3Manifest(sourcePath);
        var package = MineRewindIntegrationPackage.Resolve("");
        var pluginPath = Path.Combine(output, "plugin"); ZipFile.ExtractToDirectory(package, pluginPath);
        var manifest = PluginPackageManifestReader.Parse(await File.ReadAllBytesAsync(Path.Combine(pluginPath, "manifest.json"))).Contract;
        var context = new PackageContext(pluginPath);
        using var entry = File.OpenRead(Path.Combine(pluginPath, manifest.EntryAssembly));
        var plugin = (IFolderRewindPlugin)Activator.CreateInstance(context.LoadFromStream(entry).GetType(manifest.EntryType, true)!)!;
        var source = SourceId.New();
        var folder = new FolderSnapshot(source.Value, sourcePath, "isolated snapshot", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var config = new ConfigSnapshot(Guid.NewGuid().ToString("D"), new("acceptance"), manifest.ConfigKinds[0].Kind,
            "isolated acceptance", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        await using var temporary = await OwnedPluginTemporaryStorage.CreateAsync(Path.Combine(output, "temporary"));
        var runtime = new PluginRuntimeManager();
        var activation = await runtime.ActivateAsync(new(manifest.PluginId, () => plugin, new(manifest.PluginId, new Dictionary<string, JsonElement>()),
            [config], new Host(temporary), new ActivationStore(), manifest));
        Assert.IsTrue(activation.Success, JsonSerializer.Serialize(activation.Diagnostics));
        var codec = new SevenZipArchiveProcessBackend(() => sevenZip, () => null, false, ".restore-marker");
        var policy = new ArtifactTransformPolicy(manifest.ArtifactTransformers.Single(t => t.SupportsIncrementalInput).TransformerId,
            new Dictionary<string, JsonElement>(), ArtifactTransformFailureBehavior.RequireTransform);
        var measurements = new List<object>();
        try
        {
            using (var operation = runtime.TryAcquireOperation(manifest.PluginId, default)!)
            {
                var filePolicy = operation.Find<IFilePolicyCapability>(p => p.Kind == config.Kind)!;
                var rules = await filePolicy.ResolveAsync(new(config, folder), operation.Context);
                if (File.Exists(Path.Combine(sourcePath, "level.dat")))
                    CollectionAssert.Contains(rules.TypedRequiredExclusions.ToArray(), new PathFilterRule("session.lock", PathFilterTarget.File));
                else Assert.IsEmpty(rules.TypedRequiredExclusions, "Ordinary folders must retain their ordinary policy.");
            }
            var archive = Path.Combine(output, "core.7z");
            await Measure("core-full", () => codec.PackLogicalTreeAsync(sourcePath, archive, default));
            var coreRestore = Path.Combine(output, "core-restore");
            await Measure("core-restore", () => codec.UnpackLogicalTreeAsync(archive, coreRestore, default));
            RequireTree(sourcePath, coreRestore, true);

            if (File.Exists(Path.Combine(sourcePath, "level.dat")))
            {
                var oldStore = new NativeArtifactStore(Path.Combine(output, "legacy-store"), codec);
                PreparedNativeArtifact? prepared = null;
                await Measure("world-full-v1-transform", async () => prepared = await oldStore.PrepareAsync(runtime, config, folder,
                    "old-version", "old-representation", sourcePath, policy, false, default));
                await using (prepared!)
                {
                    await Measure("world-full-v1-envelope", () => prepared!.SealAndCommitGraphAsync(default));
                    await using var read = await prepared!.OpenAsync(default);
                    var materializer = prepared.Operation.Find<IRestoreMaterializerCapability>(_ => true)!;
                    await using var workspace = await RestoreMaterializationWorkspace.CreateAsync(Path.Combine(output, "old-restore"));
                    await Measure("world-full-v1-restore", async () =>
                    {
                        var result = await materializer.MaterializeAsync(new(config, folder, "old-version", prepared.Intent.Root, read.Artifacts,
                            RestoreMode.Clean, RestoreMode.Clean, read.Read, workspace, new AcceptanceProgress()), prepared.Operation.Context);
                        Assert.IsTrue(result.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings);
                    });
                    RequireTree(sourcePath, workspace.RootPath, false); // Full v1 stores files; Host owns directory inventory.
                }
            }
            var store = new NativeArtifactStore(Path.Combine(output, "native-store"), codec);
            var service = new NativeArtifactIncrementalCaptureService(runtime, store, false);
            SourceCaptureBaseline? baseline = null;
            // Repeated performance rounds isolate the two Full paths. Warmup/acceptance still
            // performs Smart, checkpoint and every restore on each real snapshot.
            var steps = Environment.GetEnvironmentVariable("FOLDERREWIND_PHASE3_FULL_MEASUREMENTS_ONLY") == "1" ? 1 : 3;
            for (var step = 0; step < steps; step++)
            {
                if (step == 1) await File.WriteAllBytesAsync(Path.Combine(sourcePath, "phase3-added.bin"), new byte[] { 17, 41, 99 });
                var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToImmutableSortedDictionary(
                    path => Path.GetRelativePath(sourcePath, path).Replace('\\', '/'),
                    path => new SourceCaptureFileState(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal);
                var directories = Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(sourcePath, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToImmutableArray();
                var plan = FileChangeCapturePlan.Create(files, baseline, EffectiveSourceBoundarySnapshot.All, step > 0, step == 2) with { Directories = directories };
                SourceCaptureResult? capture = null;
                var mode = step switch { 0 => "native-full", 1 => "smart", _ => "checkpoint" };
                await Measure(mode, async () => capture = await service.CaptureAsync(plan, config, folder, policy, sourcePath,
                    EffectiveSourceBoundarySnapshot.All, new(baseline?.Revision ?? -1, "", 0, files), default));
                try
                {
                    Assert.IsNotNull(capture?.RepresentationCandidate);
                    var rootId = new ArtifactId(Guid.Parse(capture.RepresentationCandidate.Metadata["artifactRootId"]));
                    var representation = capture.RepresentationCandidate.ToFact(capture.ReservedVersionId!.Value);
                    var backend = new NativePluginArtifactBackend(runtime, () => store,
                        (_, _) => Task.FromResult(new NativeArtifactBinding(representation, config, folder)));
                    var restored = Path.Combine(output, mode + "-restore");
                    await Measure(mode + "-restore", () => backend.MaterializeAsync(rootId.Value, manifest.PluginId.Value, restored, default).AsTask());
                    RequireTree(sourcePath, restored, true);
                    baseline = new(source, step, capture.ReservedVersionId.Value, representation.RepresentationId,
                        RepresentationKind.PluginArtifact, capture.PayloadCandidate!.FileName, step == 2 ? 0 : step, files,
                        EffectiveSourceBoundarySnapshot.All.Fingerprint) { ArtifactRootId = rootId.Value, Directories = directories };
                    measurements.Add(new { stage = mode + "-integrity", files = files.Count, directories = directories.Length,
                        bytes = files.Values.Sum(f => f.Size), diagnostics = capture.Diagnostics, manifest = Phase3Manifest(restored) });
                }
                finally { if (capture?.CommitParticipant is not null) await capture.CommitParticipant.AbortCommitAsync(default); }
            }
            CollectionAssert.AreEqual(original, Phase3Manifest(input), "Isolated master changed.");
            Assert.HasCount(expected.Length, Phase3Manifest(coreRestore));
            await runtime.DeactivateAsync(manifest.PluginId);
            await temporary.DeleteAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            { input, package, packageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), excluded, measurements }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await runtime.DeactivateAsync(manifest.PluginId); context.Unload(); }

        async Task Measure(string name, Func<Task> action)
        {
            var events = new ConcurrentQueue<object>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "FolderRewind.Artifacts",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => events.Enqueue(new { activity.OperationName, milliseconds = activity.Duration.TotalMilliseconds,
                    tags = activity.TagObjects.ToDictionary(p => p.Key, p => p.Value) })
            };
            ActivitySource.AddActivityListener(listener);
            using var monitor = new AcceptanceMonitor(output);
            using var diagnostic = HistoryOperationDiagnostics.Begin(name);
            var timer = Stopwatch.StartNew();
            await action();
            timer.Stop(); diagnostic.Dispose(); monitor.Dispose();
            measurements.Add(new { stage = name, elapsedMs = timer.Elapsed.TotalMilliseconds, diagnostics = diagnostic.Summary,
                monitor.PeakHandles, monitor.PeakWorkingSet, monitor.PeakTemporaryBytes, monitor.CategoryPeaks,
                monitor.SkippedSamples, samplingIntervalMs = 50, events = events.ToArray() });
        }
    }

    private static string[] Phase3Manifest(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => { using var stream = File.OpenRead(path); return Path.GetRelativePath(root, path).Replace('\\', '/') + "\t"
            + stream.Length + "\t" + Convert.ToHexString(SHA256.HashData(stream)); }).Order(StringComparer.Ordinal).ToArray();
    private static void RequireTree(string source, string target, bool directories)
    {
        CollectionAssert.AreEqual(Phase3Manifest(source), Phase3Manifest(target), target);
        if (directories) CollectionAssert.AreEquivalent(Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(source, p)).ToArray(),
            Directory.GetDirectories(target, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(target, p)).ToArray());
    }
    private sealed class AcceptanceProgress : IPluginOperationProgress { public void Report(PluginProgress progress) { } }
    private sealed class AcceptanceMonitor : IDisposable
    {
        private readonly CancellationTokenSource _stop = new(); private readonly Task _sampling;
        internal long PeakWorkingSet, PeakTemporaryBytes; internal int PeakHandles, SkippedSamples;
        internal Dictionary<string, long> CategoryPeaks { get; } = new(StringComparer.Ordinal);
        internal AcceptanceMonitor(string root) => _sampling = Task.Run(async () =>
        {
            using var process = Process.GetCurrentProcess();
            while (!_stop.IsCancellationRequested)
            {
                process.Refresh(); PeakHandles = Math.Max(PeakHandles, process.HandleCount);
                PeakWorkingSet = Math.Max(PeakWorkingSet, process.WorkingSet64 + ArtifactDiagnostics.SampleChildWorkingSet());
                var current = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var directory in new[] { "temporary", "native-store", "legacy-store" })
                {
                    var path = Path.Combine(root, directory); if (!Directory.Exists(path)) continue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                        {
                            try
                            {
                                var name = file.Replace('\\', '/');
                                var category = name.Contains("/scratch/") ? "scratch" : name.Contains("/source/") ? "input-copy"
                                    : name.Contains("/materialize-") ? "materialization-input" : name.Contains("/staging/") ? "staging" : "envelope-and-ledger";
                                current[category] = checked(current.GetValueOrDefault(category) + new FileInfo(file).Length);
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SkippedSamples++; }
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SkippedSamples++; }
                }
                foreach (var pair in current) CategoryPeaks[pair.Key] = Math.Max(CategoryPeaks.GetValueOrDefault(pair.Key), pair.Value);
                PeakTemporaryBytes = Math.Max(PeakTemporaryBytes, current.Values.Sum());
                try { await Task.Delay(50, _stop.Token); } catch (OperationCanceledException) { }
            }
        });
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; _stop.Cancel(); _sampling.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
