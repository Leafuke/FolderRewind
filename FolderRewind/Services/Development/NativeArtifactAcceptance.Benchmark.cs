#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private sealed record BenchmarkRow(string Scenario, int Iteration, bool Warmup, double BackupMilliseconds,
        long AddedStorageBytes, long UploadedBytes, double RestoreMilliseconds, ulong HostReadBytes,
        ulong HostWriteBytes, long HostPeakWorkingSet, bool ByteExact, string StateDigest,
        IReadOnlyDictionary<string, StageMeasurement> BackupStages, IReadOnlyDictionary<string, StageMeasurement> RestoreStages);

    private sealed record StageMeasurement(int Calls, double InclusiveMilliseconds, IReadOnlyDictionary<string, long> Counters);
    private sealed class StageRecorder : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, StageMeasurement> _stages = [];
        private readonly ActivityListener _listener;
        private readonly CancellationTokenSource _sampling = new();
        private readonly Task _sampler;
        private long _childPeak;
        public StageRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is "FolderRewind.Artifacts" or "MineRewind.Semantic",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        var key = activity.Source.Name + "/" + activity.OperationName;
                        _stages.TryGetValue(key, out var old);
                        var counters = old?.Counters.ToDictionary(p => p.Key, p => p.Value) ?? [];
                        foreach (var tag in activity.TagObjects)
                            if (tag.Value is long value) counters[tag.Key] = counters.GetValueOrDefault(tag.Key) + value;
                        _stages[key] = new((old?.Calls ?? 0) + 1, (old?.InclusiveMilliseconds ?? 0) + activity.Duration.TotalMilliseconds, counters);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
            _sampler = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
                try
                {
                    while (await timer.WaitForNextTickAsync(_sampling.Token))
                    {
                        var bytes = ArtifactDiagnostics.SampleChildWorkingSet();
                        lock (_gate) _childPeak = Math.Max(_childPeak, bytes);
                    }
                }
                catch (OperationCanceledException) when (_sampling.IsCancellationRequested) { }
            });
        }
        public IReadOnlyDictionary<string, StageMeasurement> Take()
        {
            lock (_gate)
            {
                var snapshot = _stages.ToDictionary(p => p.Key, p => p.Value);
                snapshot["diagnostics/children"] = new(1, 0, new Dictionary<string, long> { ["sampled.peak.workingSet.bytes"] = _childPeak });
                _stages.Clear(); _childPeak = 0; return snapshot;
            }
        }
        public void Dispose() { _listener.Dispose(); _sampling.Cancel(); _sampler.GetAwaiter().GetResult(); _sampling.Dispose(); }
    }

    private static async Task<int> RunBenchmarkAsync(Request request, BackupConfig config, string root)
    {
        using var stages = new StageRecorder();
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var archive = new SevenZipHistoryArchiveBackend(config);
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(archive),
            new SmartDeltaRepresentationHandler(archive), NativeArtifactHostService.Handler(config, history)]);
        var source = config.SourceFolders.Single().Path;
        Directory.CreateDirectory(config.DestinationPath);
        var region = Directory.EnumerateFiles(Path.Combine(source, "region"), "*.mca")
            .OrderByDescending(p => new FileInfo(p).Length).First();
        var rows = new List<BenchmarkRow>();
        var remote = new BenchmarkObjects(Path.Combine(root, "measured-remote"));
        var checkpointAfter = 0;
        config.Archive.MaxSmartBackupsPerFull = 0;
        var scenarios = request.Phase == "capture" ? new[] { "baseline", "unchanged", "sparse", "dense", "deletion", "checkpoint" }
            : new[] { "unchanged", "sparse", "dense", "deletion", "checkpoint" };
        foreach (var scenario in scenarios)
        {
            var repeats = scenario is "baseline" or "checkpoint" ? 1 : request.Iterations + 1;
            for (var iteration = 0; iteration < repeats; iteration++)
            {
                if (scenario is "sparse" or "dense" or "checkpoint")
                {
                    var changed = MutateInlineChunks(region, scenario == "dense" ? 1024 : 3);
                    if (changed < 3) throw new InvalidDataException("Fixture lacks three mutable inline chunks.");
                    File.SetLastWriteTimeUtc(region, DateTime.UtcNow.AddSeconds(rows.Count + 1));
                }
                if (scenario == "deletion")
                {
                    var candidate = Directory.EnumerateFiles(Path.Combine(source, "poi"), "*.mca").Order(StringComparer.Ordinal).First();
                    File.Delete(candidate);
                }
                if (scenario == "checkpoint")
                {
                    config.Archive.MaxSmartBackupsPerFull = 1;
                    checkpointAfter = (await history.Query.GetAllVersionsAsync()).Count;
                }
                var beforeBytes = Directory.EnumerateFiles(config.DestinationPath, "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)).Sum(p => new FileInfo(p).Length);
                GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var beforeIo);
                stages.Take();
                var watch = Stopwatch.StartNew();
                var result = await BackupService.BackupConfigAsync(config,
                    request.Incremental && scenario == "checkpoint"
                        ? BackupInvocationOptions.ForManual().WithSemanticCheckpoint() : BackupInvocationOptions.Default);
                watch.Stop(); var elapsed = watch.Elapsed.TotalMilliseconds;
                GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var afterIo);
                var backupStages = stages.Take();
                if (scenario == "unchanged" ? result.CreatedNewArchive : !result.CreatedNewArchive)
                    throw new InvalidDataException("Unexpected benchmark backup outcome: " + JsonSerializer.Serialize(result));
                var representations = await history.Query.GetAllRepresentationsAsync();
                var workspace = (await history.WorkspaceStore.LoadAsync()).Value!;
                var selected = representations.Single(r => r.VersionId == workspace.SourceBaselines.Single().BaseVersionId);
                var afterBytes = Directory.EnumerateFiles(config.DestinationPath, "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)).Sum(p => new FileInfo(p).Length);
                var beforeUpload = remote.UploadedBytes;
                if (selected.Kind == RepresentationKind.PluginArtifact)
                    await NativeArtifactHostService.Store(config).ExportObjectsAsync(
                        new(Guid.Parse(selected.RepresentationSpecificMetadata["artifactRootId"])),
                        Path.Combine(root, "measured-descriptor.json"), remote, default);
                else if (scenario != "unchanged")
                {
                    var local = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries.Single(e => e.RepresentationId == selected.RepresentationId);
                    await remote.UploadObjectAsync(selected.RepresentationId.ToString(), local.Locator.AbsolutePath!, 0, "", new(FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumBytes), default);
                }
                var restored = Path.Combine(root, "benchmark-restored");
                if (Directory.Exists(restored)) Directory.Delete(restored, true);
                watch.Restart();
                stages.Take();
                await engine.MaterializeAsync(selected.RepresentationId, representations,
                    await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact, restored);
                watch.Stop();
                var restoreStages = stages.Take();
                var expected = await MergeTreeManifest.ReadAsync(source, p => p != "session.lock", default);
                var actual = await MergeTreeManifest.ReadAsync(restored, _ => true, default);
                if (actual.Digest != expected.Digest) throw new InvalidDataException("Benchmark restore is not byte exact.");
                rows.Add(new(scenario, iteration, iteration == 0 && repeats > 1, elapsed, afterBytes - beforeBytes,
                    remote.UploadedBytes - beforeUpload, watch.Elapsed.TotalMilliseconds,
                    afterIo.ReadBytes - beforeIo.ReadBytes, afterIo.WriteBytes - beforeIo.WriteBytes,
                    Process.GetCurrentProcess().PeakWorkingSet64, true, actual.Digest, backupStages, restoreStages));
                await File.WriteAllTextAsync(Path.Combine(root, request.Phase == "capture" ? "benchmark-results.json" : "benchmark-measure-results.json"), JsonSerializer.Serialize(new
                {
                    Semantic = request.Incremental, ColdCacheControlled = false, Rows = rows,
                    CheckpointAfterVersionCount = checkpointAfter, SourceFiles = expected.Files.Count,
                    ChildProcessIoIncluded = false, UploadMeasurement = "actual copied ciphertext bytes; local verified object transport"
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        return 0;
    }

    // Acceptance-only mutator. Changes an existing long LastUpdate tag and repacks just its
    // original slot. It never uses the new semantic writer to generate the expected bytes.
    private static int MutateInlineChunks(string path, int maximum)
    {
        var bytes = File.ReadAllBytes(path); var count = 0;
        ReadOnlySpan<byte> tag = [4, 0, 10, 76, 97, 115, 116, 85, 112, 100, 97, 116, 101];
        for (var slot = 0; slot < 1024 && count < maximum; slot++)
        {
            var location = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(slot * 4));
            var offset = checked((int)(location >> 8) * 4096); var sectors = (int)(location & 255);
            if (sectors == 0 || offset < 8192 || offset + (long)sectors * 4096 > bytes.Length) continue;
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)); var compression = bytes[offset + 4];
            if (length < 1 || length > sectors * 4096 - 4 || compression is not (1 or 2 or 3)) continue;
            using var raw = new MemoryStream(bytes, offset + 5, length - 1, false);
            using Stream decoded = compression == 1 ? new GZipStream(raw, CompressionMode.Decompress)
                : compression == 2 ? new ZLibStream(raw, CompressionMode.Decompress) : raw;
            using var payload = new MemoryStream(); decoded.CopyTo(payload);
            var nbt = payload.ToArray(); var index = nbt.AsSpan().IndexOf(tag);
            if (index < 0 || index + tag.Length + 8 > nbt.Length) continue;
            var stamp = nbt.AsSpan(index + tag.Length, 8);
            BinaryPrimitives.WriteInt64BigEndian(stamp, BinaryPrimitives.ReadInt64BigEndian(stamp) + 1);
            using var encoded = new MemoryStream();
            if (compression == 3) encoded.Write(nbt);
            else
            {
                using Stream compressor = compression == 1 ? new GZipStream(encoded, CompressionLevel.Optimal, true)
                    : new ZLibStream(encoded, CompressionLevel.Optimal, true);
                compressor.Write(nbt);
            }
            var updated = encoded.ToArray();
            if (updated.Length + 5 > sectors * 4096) continue;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), updated.Length + 1);
            updated.CopyTo(bytes, offset + 5); count++;
        }
        File.WriteAllBytes(path, bytes); return count;
    }

    private sealed class BenchmarkObjects(string directory) : INativeArtifactObjectTransport
    {
        private readonly Dictionary<string, (long Size, string Hash)> _verified = [];
        public long UploadedBytes { get; private set; }
        public Task<bool> HasVerifiedObjectAsync(string key, long size, string hash, CancellationToken token)
            => Task.FromResult(_verified.TryGetValue(key, out var receipt) && receipt.Size == size
                && StringComparer.OrdinalIgnoreCase.Equals(receipt.Hash, hash)
                && new FileInfo(Path.Combine(directory, key.Replace('/', Path.DirectorySeparatorChar))) is { Exists: true } file && file.Length == size);
        public async Task UploadObjectAsync(string key, string path, long size, string hash, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token)
        {
            var target = Path.Combine(directory, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, true);
            await using var file = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (hash.Length != 0 && !StringComparer.OrdinalIgnoreCase.Equals(hash, actual)) throw new InvalidDataException("Transfer mismatch.");
            _verified[key] = (file.Length, actual);
            UploadedBytes += file.Length;
        }
        public async Task DownloadObjectAsync(string key, string path, long expectedLength,
            FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token)
        {
            await using var input = File.OpenRead(Path.Combine(directory, key.Replace('/', Path.DirectorySeparatorChar)));
            await using var output = File.Create(path);
            await budget.CopyAsync(input, output, expectedLength, token);
        }
    }
}
#endif
