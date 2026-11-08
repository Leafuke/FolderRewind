using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using MineRewind.World.Semantic;

// The same executable source is built against frozen baseline and candidate assemblies.
// Micro workloads expose removed work; they do not claim complete backup throughput.
if (args[0].StartsWith("--repair-", StringComparison.Ordinal))
{
    await ImportRepairProbe.RunAsync(args);
    return;
}
var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var regionPath = Path.GetFullPath(args[1]); var region = File.ReadAllBytes(regionPath);
var deleted = region.ToArray();
var slot = Enumerable.Range(0, 1024).First(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(region.AsSpan(i * 4, 4)) != 0);
deleted.AsSpan(slot * 4, 4).Clear(); deleted.AsSpan(4096 + slot * 4, 4).Clear();
var rows = new List<object>();
for (var round = -1; round < 3; round++)
{
    var objects = new Objects();
    var source = new Source(new() { ["level.dat"] = [1], ["region/r.0.0.mca"] = region });
    var first = WorldIncrementalFormat.Capture(source, null, objects, objects, "source", "boundary", source.Bytes.Keys.ToArray(), [], [], false, default);
    objects.NextCapture();
    var allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
    var second = WorldIncrementalFormat.Capture(new Source(new() { ["region/r.0.0.mca"] = deleted }), first,
        objects, objects, "source", "boundary", [], ["region/r.0.0.mca"], [], false, default);
    timer.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    using (var restored = WorldIncrementalFormat.OpenFile(second.Files.Single(f => f.Path == "region/r.0.0.mca"), objects, default))
    { using var bytes = new MemoryStream(); restored.CopyTo(bytes); if (!bytes.ToArray().SequenceEqual(deleted)) throw new InvalidDataException("Micro capture bytes differ."); }
    rows.Add(new { scenario = "deleted-chunk-capture", round, warmup = round < 0, elapsedMs = timer.Elapsed.TotalMilliseconds, allocatedBytes = allocated,
        stateBytes = JsonSerializer.SerializeToUtf8Bytes(second).Length, byteExact = true });
    var loaded = JsonSerializer.Deserialize<WorldIncrementalState>(JsonSerializer.Serialize(first))!;
    objects.NextCapture();
    allocated = GC.GetTotalAllocatedBytes(true); timer.Restart();
    var fromDisk = WorldIncrementalFormat.Capture(new Source(new() { ["region/r.0.0.mca"] = deleted }), loaded,
        objects, objects, "source", "boundary", [], ["region/r.0.0.mca"], [], false, default);
    timer.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    using (var restored = WorldIncrementalFormat.OpenFile(fromDisk.Files.Single(f => f.Path == "region/r.0.0.mca"), objects, default))
    { using var bytes = new MemoryStream(); restored.CopyTo(bytes); if (!bytes.ToArray().SequenceEqual(deleted)) throw new InvalidDataException("Loaded-state capture bytes differ."); }
    rows.Add(new { scenario = "loaded-state-delta", round, warmup = round < 0, elapsedMs = timer.Elapsed.TotalMilliseconds,
        allocatedBytes = allocated, stateBytes = JsonSerializer.SerializeToUtf8Bytes(fromDisk).Length, byteExact = true });
}
var folder = Guid.Parse("11111111-1111-4111-8111-111111111111");
var transport = new Transport(); var nodes = new List<ArtifactLedgerEntry>();
await using (var staging = new ArtifactTransformStagingArea(Path.Combine(output, "fixture")))
{
    var allocations = new List<ArtifactStagingAllocation>();
    for (var i = 0; i < 8; i++)
    {
        var allocation = await staging.CreateArtifactAsync(default); allocations.Add(allocation);
        var bytes = new byte[2 * 1024 * 1024]; new Random(1234 + i).NextBytes(bytes);
        await using var stream = await staging.OpenWriteAsync(allocation.Staging, "data.bin", default);
        await stream.WriteAsync(bytes);
    }
    var facts = await staging.SealAsync("artifacts");
    foreach (var allocation in allocations)
    {
        var fact = facts[allocation.Staging]; var directory = Path.Combine(output, "fixture", allocation.ArtifactId.Value.ToString("N"));
        var payload = Path.Combine(output, allocation.ArtifactId.Value.ToString("N") + ".7z");
        using (var zip = new ZipArchive(File.Create(payload), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("data.bin", CompressionLevel.NoCompression); entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var target = entry.Open(); await using var file = File.OpenRead(Path.Combine(directory, "data.bin")); await file.CopyToAsync(target);
        }
        var node = new ArtifactLedgerEntry(allocation.ArtifactId, new(new("com.cleanup.perf"), "micro"), 1,
            new(new("com.cleanup.perf"), "materializer"), "micro-config", folder, fact.ContentRelativePath,
            fact.LogicalSha256, fact.LogicalSize, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payload))), new FileInfo(payload).Length,
            ArtifactCompleteness.Complete, CoreCaptureMode.Full, [], allocation.ArtifactId.Value.ToString("N"), ArtifactAvailability.Available, ArtifactAvailability.Pending)
        { LogicalEntryCount = fact.LogicalEntryCount };
        nodes.Add(node); transport.Paths[NativeArtifactStore.CloudObjectKey(node)] = payload;
    }
}
nodes[^1] = nodes[^1] with { Dependencies = nodes.Take(nodes.Count - 1).Select(n => n.ArtifactId).ToArray() };
var root = nodes[^1]; var descriptor = Path.Combine(output, "closure.json");
await File.WriteAllBytesAsync(descriptor, JsonSerializer.SerializeToUtf8Bytes(new NativeArtifactObjectClosure(2, root.ArtifactId,
    nodes.Select(n => new NativeArtifactObject(n, new(1, false))).ToArray())));
for (var round = -1; round < 3; round++)
{
    var store = new NativeArtifactStore(Path.Combine(output, "import-" + round), new Codec());
    long peakBundle = 0;
    void Sample()
    {
        var workspace = Path.Combine(store.Root, "transfer-workspaces");
        if (!Directory.Exists(workspace)) return;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(workspace, "*.bundle", SearchOption.AllDirectories))
                try { total += new FileInfo(file).Length; } catch (FileNotFoundException) { }
        }
        catch (DirectoryNotFoundException) { /* Import completed and removed its owned workspace. */ }
        peakBundle = Math.Max(peakBundle, total);
    }
    transport.Observe = Sample;
    using var sampling = new CancellationTokenSource();
    var sampler = Task.Run(async () => { while (!sampling.IsCancellationRequested) { Sample(); await Task.Delay(1); } });
    var allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
    await store.ImportObjectsAsync(descriptor, root.ArtifactId, root.StorageSha256, root.ConfigId, folder, transport, default);
    timer.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    sampling.Cancel(); await sampler;
    rows.Add(new { scenario = "object-closure-import", round, warmup = round < 0, elapsedMs = timer.Elapsed.TotalMilliseconds,
        allocatedBytes = allocated, temporaryBundlePeakBytes = peakBundle, payloadBytes = nodes.Sum(n => n.StorageSize), nodes = nodes.Count });
}
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
{ runtime = Environment.Version.ToString(), regionPath, regionSha256 = Convert.ToHexString(SHA256.HashData(region)), rows }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(Path.Combine(output, "results.json"));

sealed class Source(Dictionary<string, byte[]> bytes) : IReadOnlyWorldSource
{
    public Dictionary<string, byte[]> Bytes => bytes;
    public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => bytes.Select(p => new SourceFile(p.Key, p.Value.Length, DateTime.UnixEpoch)).ToArray();
    public Stream OpenRead(string path) => new MemoryStream(bytes[path], false);
}
sealed class Objects : IWorldObjectReader, IWorldObjectWriter
{
    readonly Dictionary<Guid, byte[]> bytes = []; readonly Dictionary<Guid, IReadOnlyCollection<Guid>> dependencies = []; HashSet<Guid> previous = [];
    public void NextCapture() => previous = bytes.Keys.ToHashSet();
    public bool Owns(Guid id) => !previous.Contains(id) && bytes.ContainsKey(id);
    public void BeginRepack() { }
    public long GetPackLength(Guid id) => bytes[id].LongLength;
    public IReadOnlyCollection<Guid> GetDependencies(Guid id) => dependencies[id];
    public void ValidateDependency(Guid owner, Guid dependency) { if (!dependencies[owner].Contains(dependency)) throw new InvalidDataException(); }
    public Stream Open(WorldObjectReference reference, CancellationToken token)
    {
        var content = bytes[reference.ArtifactId];
        if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(content)), reference.Sha256)) throw new InvalidDataException();
        return new MemoryStream(content, false);
    }
    public WorldObjectReference Store(Stream input, long length, bool metadata, IReadOnlyCollection<Guid> declared, CancellationToken token)
    {
        var content = new byte[checked((int)length)]; input.ReadExactly(content); var id = Guid.NewGuid();
        bytes[id] = content; dependencies[id] = declared.ToArray(); return new(id, "pack.bin", 0, length, Convert.ToHexString(SHA256.HashData(content)));
    }
}
sealed class Codec : IBoundedArtifactEnvelopeCodec
{
    public Task PackAsync(string source, string target, bool encrypted, CancellationToken token) { ZipFile.CreateFromDirectory(source, target); return Task.CompletedTask; }
    public Task UnpackAsync(string payload, string target, bool encrypted, CancellationToken token) { ZipFile.ExtractToDirectory(payload, target); return Task.CompletedTask; }
    public Task UnpackAsync(string payload, string target, bool encrypted, long maximum, ArtifactExtractionBudget budget, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(payload); var bytes = zip.Entries.Sum(e => e.Length);
        if (bytes > maximum) throw new InvalidDataException(); budget.Reserve(bytes, zip.Entries.Count);
        return UnpackAsync(payload, target, encrypted, token);
    }
}
sealed class Transport : INativeArtifactObjectTransport
{
    public Dictionary<string, string> Paths { get; } = []; public Action? Observe { get; set; }
    public Task<bool> HasVerifiedObjectAsync(string key, long length, string hash, CancellationToken token) => Task.FromResult(false);
    public Task UploadObjectAsync(string key, string path, long length, string hash, ArtifactTransferBudget budget, CancellationToken token) => throw new NotSupportedException();
    public async Task DownloadObjectAsync(string key, string path, long length, ArtifactTransferBudget budget, CancellationToken token)
    { Observe?.Invoke(); await using var source = File.OpenRead(Paths[key]); await using var target = File.Create(path); await budget.CopyAsync(source, target, length, token); Observe?.Invoke(); }
}
