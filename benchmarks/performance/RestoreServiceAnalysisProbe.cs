using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

public partial class BenchApplication
{
    private sealed record RestoreBenchmarkFixture(string Root, string ConfigId, VersionId Version, string Digest,
        string PackageSha256, IReadOnlyDictionary<string, string> Payloads, string Mode);

    // Explicit benchmark-only dispatch. All source changes occur in the isolated Host root.
    private static async Task RunRestoreServiceProbeAsync(string mode)
    {
        var ownerPath = Path.Combine(Root, "restore-benchmark-owner.json");
        var plugin = new PluginId("com.folderrewind.minerewind");
        var package = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_PLUGIN") ?? throw new InvalidOperationException("Exact plugin package required.");
        var packageHash = HashFile(package);
        var global = ConfigService.CurrentConfig.GlobalSettings;
        global.EnableKnotLink = false; global.AutoStartKnotLinkServer = false;
        global.EnableNotices = false; global.EnableUpdateReminder = false; global.FileSizeWarningThresholdKB = 0;
        global.HasShownFirstLaunchGuide = true; global.HasTriggeredInitialCoreValidation = true;
        global.Plugins.AutoCheckUpdates = false;
        global.Plugins.TypedSettings[plugin.Value] = new() { ["AutoDiscoverSaves"] = JsonSerializer.SerializeToElement(false), ["PreservePlayerData"] = JsonSerializer.SerializeToElement(false) };
        global.Plugins.EnabledIntent[plugin.Value] = true;
        BackupConfig config;
        RestoreBenchmarkFixture? fixture = null;
        if (mode is "native-prepare" or "core-world")
        {
            var snapshot = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_SNAPSHOT") ?? throw new InvalidOperationException("Protected snapshot required.");
            if (!File.Exists(snapshot + ".metadata.json")) throw new InvalidOperationException("Snapshot lacks its protected-copy witness.");
            var source = Path.Combine(Root, "source"); Directory.CreateDirectory(source);
            foreach (var directory in Directory.GetDirectories(snapshot, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(source, Path.GetRelativePath(snapshot, directory)));
            foreach (var file in Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Snapshot contains a link.");
                File.Copy(file, Path.Combine(source, Path.GetRelativePath(snapshot, file)));
            }
            config = new BackupConfig { Name = "restore-performance", DestinationPath = Path.Combine(Root, "backup"),
                Kind = new() { OwnerId = plugin.Value, KindId = "minecraft-saves" }, RequiredPluginId = plugin.Value };
            config.SourceFolders.Add(new ManagedFolder { Path = source, DisplayName = "isolated world" });
            config.Archive.Mode = mode == "native-prepare" ? BackupMode.Smart : BackupMode.Full;
            config.Archive.KeepCount = 0; config.Archive.SkipIfUnchanged = false; config.Archive.MaxSmartBackupsPerFull = 0;
            // Fixed preparation settings only; the production defaults are untouched.
            config.Archive.Method = "zstd"; config.Archive.CompressionLevel = 1; config.Archive.CpuThreads = 1;
            config.Archive.BackupBeforeRestore = false; config.Archive.QuickRestoreSafetySnapshotEnabled = false;
            if (mode == "native-prepare")
            {
                config.ArtifactTransformPolicy = new() { Transformer = new() { PluginId = plugin.Value, TransformerId = "minecraft-world-full" },
                    FailureBehavior = PersistedArtifactTransformFailureBehavior.RequireTransform, ExperimentalEnabled = true };
                config.ArtifactTransformPolicy.Parameters["representation"] = JsonSerializer.SerializeToElement("incremental-v1");
            }
            ConfigService.CurrentConfig.BackupConfigs.Clear(); ConfigService.CurrentConfig.BackupConfigs.Add(config);
        }
        else if (mode == "native-restore")
        {
            fixture = JsonSerializer.Deserialize<RestoreBenchmarkFixture>(await File.ReadAllTextAsync(ownerPath))!;
            if (!StringComparer.OrdinalIgnoreCase.Equals(fixture.Root, Root) || fixture.PackageSha256 != packageHash)
                throw new IOException("Benchmark ownership/package changed.");
            config = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == fixture.ConfigId);
        }
        else throw new ArgumentException("Unknown restore benchmark mode.");
        RequireWithin(config.DestinationPath); RequireWithin(config.SourceFolders.Single().Path);
        bool safetySnapshot = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_SAFETY") == "1";
        config.Archive.BackupBeforeRestore = safetySnapshot;
        config.Archive.RestorePreflightMode = Policy == "Strict" ? RestoreArchivePreflightMode.Strict : RestoreArchivePreflightMode.Balanced;
        if (!(await ConfigService.SaveAsync()).Success) throw new IOException("Isolated config save failed.");
        await new PluginPackageInstaller(PluginV3PackageService.PluginsRoot).InstallAsync(package, PluginInstallProvenance.Manual, packageHash);
        await PluginV3PackageService.InitializeAsync();
        if (!PluginV3RuntimeService.IsActive(plugin))
        {
            var activated = await PluginV3PackageService.SetEnabledAsync(plugin, true);
            if (!activated.Success) throw new IOException("Shipped plugin could not activate.");
        }
        var folder = config.SourceFolders.Single();
        if (fixture is not null && Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_MULTISOURCE") == "1")
        {
            await RunNativeMultiSourceRestoreAsync(config, fixture);
            return;
        }
        if (fixture is null)
        {
            // Modern worlds may place overworld regions under a namespaced dimension directory.
            var region = mode == "native-prepare" ? Directory.GetFiles(folder.Path, "*.mca", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "region")
                .OrderByDescending(p => new FileInfo(p).Length).FirstOrDefault(p => MutateRestoreChunks(p, 3, apply: false) == 3)
                ?? throw new IOException("World lacks a region with three mutable inline chunks.") : null;
            for (int layer = 0; layer < (mode == "native-prepare" ? 6 : 1); layer++)
            {
                if (layer > 0)
                {
                    if (MutateRestoreChunks(region!, 3) != 3) throw new IOException("World lacks three mutable inline chunks.");
                    File.SetLastWriteTimeUtc(region!, DateTime.UtcNow.AddSeconds(layer + 1));
                }
                var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                if (!result.CreatedNewArchive) throw new IOException("Benchmark capture did not create a version: " + result.Outcome);
            }
            var preparedHistory = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
            var version = (await preparedHistory.WorkspaceStore.LoadAsync()).Value!.GetSourceState(new SourceId(Guid.Parse(folder.Id))).BaseVersionId!.Value;
            var representations = await preparedHistory.Query.GetRepresentationsAsync(version);
            if (mode == "native-prepare" && (representations.Count != 1 || representations[0].Kind != RepresentationKind.PluginArtifact))
                throw new IOException("Native benchmark did not produce one Native representation.");
            var digest = (await MergeTreeManifest.ReadAsync(folder.Path, path => path != "session.lock", default)).Digest;
            fixture = new(Root, config.Id, version, digest, packageHash, PayloadHashes(config.DestinationPath), mode);
            await File.WriteAllTextAsync(ownerPath, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
            if (mode == "native-prepare") { await LogService.FlushAsync(); return; }
        }
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_SERVICE_RESULT") ?? throw new InvalidOperationException("New result path required.");
        if (File.Exists(output)) throw new IOException("Result already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var native = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService", true)!;
        var restoreMethod = native.GetMethod("RestoreVersionAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var stages = new ConcurrentDictionary<string, (int Calls, double Ms)>();
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name is "FolderRewind.Artifacts" or "MineRewind.Semantic",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stages.AddOrUpdate(activity.OperationName, (1, activity.Duration.TotalMilliseconds),
                (_, old) => (old.Calls + 1, old.Ms + activity.Duration.TotalMilliseconds))
        };
        ActivitySource.AddActivityListener(listener);
        var rows = new List<object>();
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            File.WriteAllText(Path.Combine(folder.Path, "__restore_probe_extra.txt"), "must be removed by Clean");
            await LogService.FlushAsync(); stages.Clear();
            var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
            var result = await (Task<HistoryRestoreResult>)restoreMethod.Invoke(null,
                [config, folder, fixture.Version, BackupService.RestoreMode.Clean, CancellationToken.None, null, null])!;
            double ready = clock.Elapsed.TotalMilliseconds;
            await (Task)typeof(HistoryRuntime).GetMethod("DrainRestoreCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history, [CancellationToken.None])!;
            await LogService.FlushAsync(); clock.Stop();
            long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
            var measuredStages = stages.ToDictionary(p => p.Key, p => new { calls = p.Value.Calls, milliseconds = p.Value.Ms });
            var diagnostic = HistoryOperationDiagnostics.GetRecent().LastOrDefault(d => d.Operation == "ordinary-restore");
            if (!result.Succeeded) throw new IOException("Ordinary restore failed: " + result.Diagnostic);
            var digest = (await MergeTreeManifest.ReadAsync(folder.Path, path => path != "session.lock", default)).Digest;
            if (digest != fixture.Digest || File.Exists(Path.Combine(folder.Path, "__restore_probe_extra.txt"))) throw new IOException("Restored world digest differs.");
            var actual = PayloadHashes(config.DestinationPath);
            if ((!safetySnapshot && actual.Count != fixture.Payloads.Count) || fixture.Payloads.Any(p => !actual.TryGetValue(p.Key, out var hash) || hash != p.Value))
                throw new IOException("Frozen backup payload changed during restore.");
            if (safetySnapshot && (diagnostic?.InclusivePhases.ContainsKey("safety-snapshot-protect") != true || actual.Count <= fixture.Payloads.Count))
                throw new IOException("Native protection snapshot was not exercised.");
            rows.Add(new { repeat, fileReadyMs = ready, totalMs = clock.Elapsed.TotalMilliseconds, allocatedBytes,
                correct = true, diagnostic, stages = measuredStages });
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { fixture.Mode, fixture.PackageSha256, safetySnapshot,
                fixture.Payloads, assemblySha256 = HashFile(typeof(BackupService).Assembly.Location), cache = "uncontrolled", rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        static void RequireWithin(string path)
        { if (!Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Benchmark path escaped isolated root."); }
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task RunNativeMultiSourceRestoreAsync(BackupConfig config, RestoreBenchmarkFixture fixture)
    {
        var first = config.SourceFolders.Single();
        var second = new ManagedFolder { Path = Path.Combine(Root, "second-source"), DisplayName = "second isolated world" };
        Directory.CreateDirectory(second.Path);
        foreach (var directory in Directory.GetDirectories(first.Path, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(second.Path, Path.GetRelativePath(first.Path, directory)));
        foreach (var file in Directory.GetFiles(first.Path, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(second.Path, Path.GetRelativePath(first.Path, file)));
        config.SourceFolders.Add(second);
        if (!(await ConfigService.SaveAsync()).Success) throw new IOException("Cannot save multi-source fixture.");
        if (!(await BackupService.BackupFolderAsync(config, second, BackupInvocationOptions.ForInternal())).CreatedNewArchive)
            throw new IOException("Second Native Source capture failed.");
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var selections = new Dictionary<SourceId, VersionId> { [new(Guid.Parse(first.Id))] = fixture.Version,
            [new(Guid.Parse(second.Id))] = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(new(Guid.Parse(second.Id))).BaseVersionId!.Value };
        foreach (var version in selections.Values)
        {
            var reps = await history.Query.GetRepresentationsAsync(version);
            if (reps.Count != 1 || reps[0].Kind != RepresentationKind.PluginArtifact) throw new IOException("Expected Native Sources in one store.");
        }
        var method = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService", true)!
            .GetMethod("RestoreSelectionsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        int opens = 0;
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == "FolderRewind.Artifacts",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (activity.OperationName == "closure.open.wall") Interlocked.Increment(ref opens); }
        };
        ActivitySource.AddActivityListener(listener);
        var rows = new List<object>();
        for (int repeat = 0; repeat < 2; repeat++)
        {
            opens = 0;
            foreach (var folder in config.SourceFolders) File.WriteAllText(Path.Combine(folder.Path, "__multi_extra"), "delete");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await (Task<HistoryRestoreResult>)method.Invoke(null, [config, selections, BackupService.RestoreMode.Clean, timeout.Token])!;
            if (!result.Succeeded || result.AppliedSources.Count != 2 || opens != 2)
                throw new IOException($"Multi-source Native restore failed: {result.Diagnostic}; closure opens={opens}.");
            await (Task)typeof(HistoryRuntime).GetMethod("DrainRestoreCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history, [CancellationToken.None])!;
            foreach (var folder in config.SourceFolders)
                if ((await MergeTreeManifest.ReadAsync(folder.Path, p => p != "session.lock", default)).Digest != fixture.Digest)
                    throw new IOException("Multi-source restored digest differs.");
            rows.Add(new { repeat, sources = 2, closureOpens = opens, correct = true });
        }
        await LogService.FlushAsync();
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_SERVICE_RESULT")!,
            JsonSerializer.Serialize(new { scenario = "same-store-two-native-sources", rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static Dictionary<string, string> PayloadHashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetExtension(p) is ".7z" or ".bin" or ".zip").Order(StringComparer.Ordinal)
        .ToDictionary(p => Path.GetRelativePath(root, p), HashFile, StringComparer.Ordinal);

    // Same sparse, valid NBT mutation as the existing Native acceptance fixture, on the isolated copy only.
    private static int MutateRestoreChunks(string path, int maximum, bool apply = true)
    {
        var bytes = File.ReadAllBytes(path); int count = 0;
        ReadOnlySpan<byte> tag = [4, 0, 10, 76, 97, 115, 116, 85, 112, 100, 97, 116, 101];
        for (int slot = 0; slot < 1024 && count < maximum; slot++)
        {
            var location = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(slot * 4));
            int offset = checked((int)(location >> 8) * 4096), sectors = (int)(location & 255);
            if (sectors == 0 || offset < 8192 || offset + (long)sectors * 4096 > bytes.Length) continue;
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)); byte compression = bytes[offset + 4];
            if (length < 1 || length > sectors * 4096 - 4 || compression is not (1 or 2 or 3)) continue;
            using var raw = new MemoryStream(bytes, offset + 5, length - 1, false);
            using Stream decoded = compression == 1 ? new GZipStream(raw, CompressionMode.Decompress) : compression == 2 ? new ZLibStream(raw, CompressionMode.Decompress) : raw;
            using var payload = new MemoryStream(); decoded.CopyTo(payload); var nbt = payload.ToArray(); int index = nbt.AsSpan().IndexOf(tag);
            if (index < 0 || index + tag.Length + 8 > nbt.Length) continue;
            var stamp = nbt.AsSpan(index + tag.Length, 8); BinaryPrimitives.WriteInt64BigEndian(stamp, BinaryPrimitives.ReadInt64BigEndian(stamp) + 1);
            using var encoded = new MemoryStream();
            if (compression == 3) encoded.Write(nbt);
            else { using Stream compressor = compression == 1 ? new GZipStream(encoded, CompressionLevel.Optimal, true) : new ZLibStream(encoded, CompressionLevel.Optimal, true); compressor.Write(nbt); }
            var updated = encoded.ToArray(); if (updated.Length + 5 > sectors * 4096) continue;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), updated.Length + 1); updated.CopyTo(bytes, offset + 5); count++;
        }
        if (apply) File.WriteAllBytes(path, bytes); return count;
    }
}
