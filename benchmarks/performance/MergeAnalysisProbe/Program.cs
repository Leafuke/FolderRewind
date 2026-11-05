using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind;

// Analyze-only adapter; all source streams are read-only. Use the same source for both exact packages.
// Usage: probe <base-root> <ours-root> <theirs-root> <raw|verified> [conservative|terrain-first]
// Coverage is asserted by the operator for these complete offline source directories.
// MERGE_PROBE_TRANSCRIPT writes the full semantic transcript outside the timed interval.
// MERGE_PROBE_CANCEL_MS requests cooperative cancellation during Analyze.
if (args.Length == 3 && args[0] == "spatial") { await SpatialProbe.Run(args[1], args[2]); return; }
if (args.Length == 2 && args[0] == "scan") { await ScanProbe.Run(args[1]); return; }
if (args.Length is < 4 or > 5 || args[3] is not ("raw" or "verified"))
    throw new ArgumentException("Expected <base-root> <ours-root> <theirs-root> <raw|verified> [policy]");
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var token = cancel.Token;
var policy = args.Length == 5 ? args[4] : "conservative";
if (policy is not ("conservative" or "terrain-first")) throw new ArgumentException("Invalid policy");
var inventoryTimer = Stopwatch.StartNew();
var inventories = new Dictionary<string, Entry[]>(StringComparer.OrdinalIgnoreCase);
var views = new List<View>();
foreach (var rootArgument in args.Take(3))
{
    var root = Path.GetFullPath(rootArgument);
    if (!inventories.TryGetValue(root, out var entries))
    {
        entries = await View.Inventory(root, token);
        inventories.Add(root, entries);
    }
    views.Add(new View(root, entries, args[3] == "verified"));
}
inventoryTimer.Stop();
Console.Error.WriteLine($"Inventory complete: {inventoryTimer.Elapsed.TotalSeconds:F3}s; {views.Sum(v => v.Entries.Length)} entries across 3 views.");
var plugin = new MinecraftSavesPlugin();
await plugin.ActivateAsync(new Activation(), token);
MergeVersionInput Input(int side) => new("version-" + side, "representation-" + side, "probe-tree-" + side,
    new(1, MergeBoundaryScope.All, [], MergeBoundaryFilter.Blacklist, [], false, "offline-complete"), views[side]);
var request = new MergeAnalysisRequest(Guid.Parse("00000000-0000-0000-0000-000000000001"),
    Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003"),
    new(new(MinecraftSavesPlugin.PluginIdentity), MinecraftSavesPlugin.MinecraftKindIdentity), policy,
    policy == "conservative" ? "4" : "5", Input(0), Input(1), Input(2), MergeLimits.Default);
using var process = Process.GetCurrentProcess();
var allocatedBefore = GC.GetTotalAllocatedBytes(true);
var cpuBefore = process.TotalProcessorTime;
var timer = Stopwatch.StartNew();
var plan = new Plan(timer);
Console.Error.WriteLine("Analyze begin"); Console.Error.Flush();
if (int.TryParse(Environment.GetEnvironmentVariable("MERGE_PROBE_CANCEL_MS"), out var cancelMs)) cancel.CancelAfter(cancelMs);
MergeProviderResult result;
try
{
    result = await ((IMergeProviderCapability)plugin).AnalyzeAsync(request, plan,
        new(new(MinecraftSavesPlugin.PluginIdentity), null!, token, default));
}
catch (OperationCanceledException) when (token.IsCancellationRequested)
{
    timer.Stop();
    await plugin.DeactivateAsync(default);
    Console.WriteLine(JsonSerializer.Serialize(new { Kind = "plugin-analyze-cancel", Policy = policy,
        RequestedAfterMs = cancelMs, ElapsedMs = timer.Elapsed.TotalMilliseconds,
        DrainMs = Math.Max(0, timer.Elapsed.TotalMilliseconds - cancelMs) }));
    return;
}
timer.Stop();
var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
process.Refresh();
var peakWorkingSet = process.PeakWorkingSet64;
var transcript = plan.Transcript(result);
var transcriptBytes = JsonSerializer.SerializeToUtf8Bytes(transcript);
var transcriptSha256 = Convert.ToHexString(SHA256.HashData(transcriptBytes));
if (Environment.GetEnvironmentVariable("MERGE_PROBE_TRANSCRIPT") is { Length: > 0 } transcriptPath)
    await File.WriteAllBytesAsync(transcriptPath, transcriptBytes);
Console.WriteLine(JsonSerializer.Serialize(new
{
    Kind = "plugin-analyze-only", Mode = args[3], Policy = policy,
    PluginAssembly = typeof(MinecraftSavesPlugin).Assembly.Location,
    PluginAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MinecraftSavesPlugin).Assembly.Location))),
    InputRoots = args.Take(3), InventoryElapsedSeconds = inventoryTimer.Elapsed.TotalSeconds,
    InventoryDistinctRoots = inventories.Count, AnalysisElapsedSeconds = timer.Elapsed.TotalSeconds,
    AnalysisCpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds, AllocatedBytes = allocated,
    PeakWorkingSetBytes = peakWorkingSet, TranscriptSha256 = transcriptSha256, result.Outcome,
    plan.Groups, plan.WholeWorldGroups, plan.Subjects, plan.Automatic, plan.CanOverrideAutomatic, plan.Blocked, plan.ClaimedPaths,
    plan.PlanBytesWritten, plan.PlanBytesRead, plan.FirstGroupSeconds, plan.FirstSubjectsSeconds,
    result.Diagnostics,
    Views = views.Select(v => new { Files = v.Entries.Length, v.Opens, v.ContentBytesRead, v.ContentReadCalls,
        v.VerificationBytesRead, v.VerificationSeconds, v.Pages, v.CoverageQueries }),
    Limitation = "In-memory plan writer and operator-asserted complete coverage; excludes Host preparation, SQL persistence, preview, Build and Apply. Inventory warms filesystem cache."
}, new JsonSerializerOptions { WriteIndented = true }));
await plugin.DeactivateAsync(default);

sealed class Activation : IPluginActivationContext
{
    public PluginId PluginId => new(MinecraftSavesPlugin.PluginIdentity);
    public PluginSettingsSnapshot Settings => new(PluginId, new Dictionary<string, JsonElement>());
    public IReadOnlyList<ConfigSnapshot> Configs => [];
    public void RegisterCapability<T>(T capability) where T : class, IPluginCapability { }
}
sealed record Entry(string Relative, long Length, string Digest);
sealed class View(string root, Entry[] entries, bool verify) : IMergeSourceView
{
    public Entry[] Entries => entries;
    private readonly Dictionary<string, Entry> _byPath = entries.ToDictionary(e => e.Relative, StringComparer.Ordinal);
    public long Opens, ContentBytesRead, ContentReadCalls, VerificationBytesRead, Pages, CoverageQueries;
    public double VerificationSeconds;
    public static async Task<Entry[]> Inventory(string root, CancellationToken token)
    {
        var found = new List<Entry>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked directory");
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked input");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(child); continue; }
                await using var file = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                var digest = await Digest(file, token);
                found.Add(new(Path.GetRelativePath(root, child).Replace('\\', '/'), file.Length, digest));
            }
        }
        return found.OrderBy(e => e.Relative, StringComparer.Ordinal).ToArray();
    }
    private static async Task<string> Digest(Stream input, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("folderrewind/file/1\0"));
        var buffer = new byte[65536]; int n;
        while ((n = await input.ReadAsync(buffer, token)) != 0) hash.AppendData(buffer, 0, n);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public ValueTask<MergeInputPage> ReadPageAsync(string? cursor, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Pages++;
        var start = int.Parse(cursor ?? "0", CultureInfo.InvariantCulture);
        var page = entries.Skip(start).Take(count).Select(e => new MergeInputEntry(e.Relative, false, e.Length, e.Digest)).ToArray();
        return ValueTask.FromResult(new MergeInputPage(page, start + page.Length < entries.Length ? (start + page.Length).ToString(CultureInfo.InvariantCulture) : null));
    }
    public ValueTask<MergeCoverageResult> CheckCoverageAsync(MergeCoverageQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CoverageQueries++;
        return ValueTask.FromResult(new MergeCoverageResult(query.RequirementId, "offline-complete", MergeCoverageState.Complete, []));
    }
    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var entry = _byPath[path];
        var file = new FileStream(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        try
        {
            Opens++;
            if (verify)
            {
                var watch = Stopwatch.StartNew();
                if (file.Length != entry.Length || await Digest(file, token) != entry.Digest) throw new IOException("Input changed");
                VerificationBytesRead += file.Position; VerificationSeconds += watch.Elapsed.TotalSeconds; file.Position = 0;
            }
            return new Counted(file, n => { ContentBytesRead += n; ContentReadCalls++; });
        }
        catch { await file.DisposeAsync(); throw; }
    }
}
sealed class Counted(Stream inner, Action<int> count) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int size) { var n = inner.Read(buffer, offset, size); count(n); return n; }
    public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); count(n); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var n = await inner.ReadAsync(buffer, token); count(n); return n; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int size, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, size), token).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int size) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}
sealed class Plan(Stopwatch timer) : IMergePlanWriter
{
    private readonly Dictionary<string, byte[]> _data = [];
    private readonly HashSet<string> _groups = [];
    private readonly HashSet<(string Group, string Subject)> _subjects = [];
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly List<MergeOutputGroup> _groupRecords = [];
    private readonly List<MergeSubject> _subjectRecords = [];
    private readonly List<object> _claims = [];
    public long Groups, WholeWorldGroups, Subjects, Automatic, CanOverrideAutomatic, Blocked, ClaimedPaths, PlanBytesWritten, PlanBytesRead;
    public double? FirstGroupSeconds, FirstSubjectsSeconds;
    public ValueTask AddGroupAsync(MergeOutputGroup group, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (!_groups.Add(group.GroupId)) throw new InvalidOperationException("Duplicate group"); _groupRecords.Add(group); FirstGroupSeconds ??= timer.Elapsed.TotalSeconds; Groups++; if (group.State.TryGetProperty("WholeWorld", out var whole) && whole.GetBoolean()) WholeWorldGroups++; return ValueTask.CompletedTask; }
    public ValueTask ClaimPathsAsync(string group, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, CancellationToken token)
    {
        if (!_groups.Contains(group)) throw new InvalidOperationException("Missing group");
        foreach (var path in inputs.Concat(outputs)) { if (_owners.TryGetValue(path, out var owner) && owner != group) throw new InvalidOperationException("Conflicting owner"); _owners[path] = group; }
        token.ThrowIfCancellationRequested(); _claims.Add(new { Group = group, Inputs = inputs.ToArray(), Outputs = outputs.ToArray() });
        ClaimedPaths += inputs.Count; return ValueTask.CompletedTask;
    }
    public ValueTask AddSubjectsAsync(IReadOnlyList<MergeSubject> subjects, CancellationToken token)
    {
        foreach (var subject in subjects) if (!_groups.Contains(subject.GroupId) || !_subjects.Add((subject.GroupId, subject.SubjectId))) throw new InvalidOperationException("Invalid subject identity");
        token.ThrowIfCancellationRequested(); _subjectRecords.AddRange(subjects);
        FirstSubjectsSeconds ??= timer.Elapsed.TotalSeconds; Subjects += subjects.Count; Automatic += subjects.Count(s => s.IsAutomatic); CanOverrideAutomatic += subjects.Count(s => s.CanOverrideAutomatic); Blocked += subjects.Count(s => s.IsBlocked); return ValueTask.CompletedTask;
    }
    public ValueTask<Stream> OpenDataWriteAsync(string key, CancellationToken token) => ValueTask.FromResult<Stream>(new Capture(bytes => { _data.Add(key, bytes); PlanBytesWritten += bytes.Length; if (_data.Count % 10 == 0) { Console.Error.WriteLine($"Analyze coordinate plans={_data.Count} elapsed={timer.Elapsed.TotalSeconds:F3}s"); Console.Error.Flush(); } }));
    public ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken token) { var bytes = _data[key]; PlanBytesRead += bytes.Length; return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false)); }
    public object Transcript(MergeProviderResult result) => new
    {
        result.Outcome, result.Diagnostics,
        Groups = _groupRecords, Subjects = _subjectRecords, Claims = _claims,
        Data = _data.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new
        {
            p.Key, Bytes = p.Value.Length, Sha256 = Convert.ToHexString(SHA256.HashData(p.Value)),
            // Coordinate plans contain the complete region/chunk fingerprints, not decoded payloads.
            Content = PlanContent(p.Value)
        }).ToArray()
    };
    private static JsonElement PlanContent(byte[] bytes)
    {
        try { using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(bytes)); }
    }
}
sealed class Capture(Action<byte[]> closed) : MemoryStream
{
    private bool _closed;
    protected override void Dispose(bool disposing) { if (disposing && !_closed) { _closed = true; closed(ToArray()); } base.Dispose(disposing); }
}
static class SpatialProbe
{
    public static async Task Run(string left, string right)
    {
        var plugin = new MinecraftSavesPlugin(); await plugin.ActivateAsync(new Activation(), default);
        var folder = new FolderSnapshot(Guid.NewGuid(), left, "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var kind = new ConfigKindRef(new(MinecraftSavesPlugin.PluginIdentity), MinecraftSavesPlugin.MinecraftKindIdentity);
        var config = new ConfigSnapshot("probe", new("1"), kind, "probe", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        SpatialPreviewSource Input(string path) => new(config, folder, Guid.NewGuid()) { FixedInput = new(path, path, "offline-fixed") { Coverage = new Coverage() } };
        var request = new SpatialComparisonRequest(Guid.NewGuid(), Input(left), Input(right), null, 500);
        var capability = (ISpatialComparisonCapability)plugin;
        var context = new PluginInvocationContext(new(MinecraftSavesPlugin.PluginIdentity), null!, default, default);
        using var process = Process.GetCurrentProcess();
        var allocated = GC.GetTotalAllocatedBytes(true); var cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew(); double? firstPage = null; long changes = 0, pages = 0, scanned = 0;
        string? cursor = null;
        var kinds = new Dictionary<string, int>();
        var details = new List<SpatialChange>();
        do
        {
            var page = await capability.CompareAsync(request with { Cursor = cursor }, context);
            firstPage ??= timer.Elapsed.TotalSeconds;
            pages++; changes += page.Changes.Count; scanned = page.ScannedFiles; cursor = page.NextCursor;
            details.AddRange(page.Changes);
            foreach (var change in page.Changes) { var key = change.Kind.ToString(); kinds[key] = kinds.GetValueOrDefault(key) + 1; }
        } while (cursor is not null);
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { Kind = "spatial-comparison", Left = left, Right = right,
            ElapsedSeconds = timer.Elapsed.TotalSeconds, FirstPageSeconds = firstPage, CpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, Pages = pages, Changes = changes, ScannedFiles = scanned, ChangeKinds = kinds, Details = details,
            Limitation = "Different capability, two inputs, not a Merge Analyze AB; excludes Host preparation and persistence." }, new JsonSerializerOptions { WriteIndented = true }));
        await capability.CloseComparisonAsync(request.ComparisonId, context); await plugin.DeactivateAsync(default);
    }
    private sealed class Coverage : ISpatialInputCoverage { public SpatialPathPresence InspectPath(string relativePath) => SpatialPathPresence.Absent; }
}
static class ScanProbe
{
    public static async Task Run(string root)
    {
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var summaries = new List<object>(); var watch = Stopwatch.StartNew();
        foreach (var path in paths.Where(p => p.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)).Order())
        {
            if (!MineRewind.World.Semantic.MergeRegion.TryAddress(path, [""], out var address)) continue;
            var region = await MineRewind.World.Semantic.MergeRegion.ReadAsync(path, address!, (name, token) => ValueTask.FromResult<Stream>(File.OpenRead(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)))), paths, default, false, true);
            var unknown = region.Chunks.Where(c => !c.SeparatedTerrain).ToArray();
            summaries.Add(new { Path = path, Component = address!.Component.ToString(), Chunks = region.Chunks.Count, UnknownTerrain = unknown.Length, Embedded = region.Chunks.Count(c => c.HasEmbeddedEntities),
                Examples = unknown.Take(2).Select(c => Inspect(root, path, address!, c.Slot)).ToArray() });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Kind = "region-scan-without-content-fingerprint", Seconds = watch.Elapsed.TotalSeconds, Regions = summaries }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Inspect(string root, string path, MineRewind.World.Semantic.RegionAddress address, int slot)
    {
        var key = new MineRewind.World.ChunkKey(address.X * 32 + slot % 32, address.Z * 32 + slot / 32);
        var record = MineRewind.World.AnvilReader.Read(root, Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), key);
        if (record.Nbt is null) return new { Slot = slot, State = record.State.ToString() };
        using var input = new MemoryStream(record.Nbt); var nbt = new fNbt.NbtFile(); nbt.LoadFromStream(input, fNbt.NbtCompression.None);
        return new { Slot = slot, RootFields = nbt.RootTag.Select(t => new { t.Name, Type = t.TagType.ToString(), Value = t.Name is "DataVersion" or "xPos" or "zPos" && t is fNbt.NbtInt value ? (int?)value.Value : null }).ToArray() };
    }
}
