using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;

// Explicitly isolated metadata workload. No real user data or archive processes are accessed.
var outputRoot = Path.GetFullPath(args.ElementAtOrDefault(0) ?? Path.Combine(AppContext.BaseDirectory, "results"));
var replayRepository = args.ElementAtOrDefault(2) is { } replayArgument
    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(replayArgument)) : null;
if (replayRepository is not null)
    ReplayCorpus.RequireSeparateOutput(outputRoot, replayRepository);
Directory.CreateDirectory(outputRoot);
var runRoot = Path.Combine(outputRoot, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
var sizes = args.Length > 1 ? args[1].Split(',').Select(int.Parse).ToArray() : [10, 100, 500, 1000, 2000];
if (sizes.Any(n => n < 1 || n > 20000)) throw new ArgumentOutOfRangeException(nameof(sizes));
if (replayRepository is not null && sizes.Length != 1)
    throw new ArgumentException("Corpus replay requires exactly one initial pack count.");
var rows = new List<object>();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
foreach (var size in sizes)
{
    var root = Path.Combine(runRoot, "packs-" + size);
    Directory.CreateDirectory(root);
    var replay = replayRepository is null ? null : await ReplayCorpus.LoadAsync(replayRepository, root, size);
    var config = replay?.ConfigId ?? new HistoryConfigId(Guid.NewGuid().ToString("N"));
    var source = replay?.SourceId ?? SourceId.New();
    var branch = BranchId.New();
    var paths = new HistoryRepositoryPaths(Path.Combine(root, "repository"));
    var codec = new HistoryPackCodec();
    var catalog = replay?.Catalog.Entries.ToList() ?? new List<LocalReplicaCatalogEntry>();
    var payload = Path.Combine(root, "metadata-only-payload.fixture");
    if (replay is null)
        await File.WriteAllTextAsync(payload, "Metadata browsing never opens archive content.");
    VersionId? previousVersion = null;
    CheckpointId? previousCheckpoint = null;
    BranchUpdateId? previousUpdate = null;
    var originTime = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    HistoryCommitPack MakePack(int ordinal)
    {
        var time = originTime.AddSeconds(ordinal);
        var version = new SourceVersion(VersionId.New(), config, source,
            previousVersion is { } parent ? new[] { parent } : Array.Empty<VersionId>(), time, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, Array.Empty<HistoryDiagnostic>(),
            new SourceDescriptorSnapshot("Performance source", root), null, HistoryProvenance.Native("performance-probe"));
        var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId,
            RepresentationKind.CoreFull, "7z", Array.Empty<RepresentationId>(), MaterializationFidelity.Exact, null, null,
            new Dictionary<string, string> { ["fileName"] = $"capture-{ordinal:D6}.7z" });
        var checkpoint = new SourceCheckpoint(CheckpointId.New(), config, time, null, HistoryProvenance.Native("performance-probe"),
            new[] { new CheckpointSource(source, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured) },
            previousCheckpoint is { } checkpointParent ? new[] { checkpointParent } : Array.Empty<CheckpointId>());
        var update = new BranchUpdate(BranchUpdateId.New(), branch,
            previousUpdate is { } updateParent ? new[] { updateParent } : Array.Empty<BranchUpdateId>(), "main",
            checkpoint.CheckpointId, false, time, previousUpdate is null ? BranchUpdateReason.Created : BranchUpdateReason.Backup, source);
        previousVersion = version.VersionId; previousCheckpoint = checkpoint.CheckpointId; previousUpdate = update.UpdateId;
        catalog.Add(new(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload), time));
        return new(PackId.New(), HistoryTransactionId.New(), time,
            new object[] { version, representation, checkpoint, update }.Select(item => codec.CreateObject(item)));
    }

    await using (var runtime = new HistoryRuntime(new FileHistoryRepository(config, paths)))
    {
        await runtime.InitializeAsync();
        var packs = replay is null
            ? Enumerable.Range(0, size).Select(MakePack).Select(p => (ReadOnlyMemory<byte>)codec.Encode(p)).ToArray()
            : replay.Packs.Take(size).Select(p => (ReadOnlyMemory<byte>)p.File.Bytes).ToArray();
        // Batch seeding keeps setup practical; measured commits are individual append operations.
        await runtime.Repository.ImportAsync(packs);
        var seedCatalog = replay is null ? catalog : replay.GetSeedCatalog(size);
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(config, 0, seedCatalog), LocalReplicaCatalogStore.MissingRevision);
        for (var repeat = 0; repeat < 5; repeat++)
        {
            var pack = replay is null ? MakePack(size + repeat) : codec.Decode(replay.Packs[size + repeat].File.Bytes).Pack;
            await Measure("single-commit", size, repeat, size + repeat, () => runtime.Repository.CommitAsync(pack));
        }
        var stored = await runtime.Repository.ReadAllPacksAsync();
        if (stored.Count != size + 5 || stored.Sum(p => p.Pack.Objects.Length) != (size + 5) * 4)
            throw new InvalidDataException("Seed failed to grow distinct packs and version facts.");
        replay?.VerifyInstalledPacks(stored);
        var loaded = (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!;
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(config, loaded.CatalogRevision + 1, catalog), loaded.CatalogRevision);
    }
    await using (var runtime = new HistoryRuntime(new FileHistoryRepository(config, paths)))
    {
        await Measure("reopen-initialize", size, 0, size + 5, () => runtime.InitializeAsync());
        var phases = new Dictionary<string, double>();
        var query = new HistoryPresentationQueryService(runtime, (name, elapsed) => phases[name] = elapsed.TotalMilliseconds);
        var request = new HistoryPageRequest(source, false, true, null, "", 100);
        HistoryPresentationPage? page = null;
        for (var repeat = 0; repeat < 6; repeat++)
        {
            phases.Clear();
            await Measure(repeat == 0 ? "query-first-after-reopen" : "query-hot", size, repeat, size + 5,
                async () => { page = await query.QueryPageAsync(request); }, phases);
            if (page!.TotalCount != size + 5 || page.Snapshot.Timeline.Length != Math.Min(100, size + 5))
                throw new InvalidDataException($"Unexpected query results: total={page.TotalCount}, rows={page.Snapshot.Timeline.Length}");
        }
        if (page!.Next is { } next)
            await Measure("query-next-page", size, 0, size + 5, async () => { _ = await query.QueryPageAsync(request with { Cursor = next }); });
        await Measure("query-keyword", size, 0, size + 5,
            async () => { _ = await query.QueryPageAsync(request with { Keyword = "capture-000" }); });
    }
    if (replay is not null)
        await replay.VerifyAndWriteEvidenceAsync(paths, runRoot);
    await WriteResults();
    Console.WriteLine($"Completed {size} initial packs; results: {Path.Combine(runRoot, "results.json")}");
}
await WriteResults();

async Task Measure(string scenario, int size, int repeat, int actualPacks, Func<Task> operation,
    Dictionary<string, double>? phases = null)
{
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
    var watch = Stopwatch.StartNew();
    var diagnostic = HistoryOperationDiagnostics.Begin(scenario);
    using (diagnostic) await operation();
    watch.Stop();
    rows.Add(new { scenario, initialPacks = size, actualPacks, repeat, elapsedMs = watch.Elapsed.TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
        gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
        phases = phases is null ? null : new Dictionary<string, double>(phases), diagnostic = diagnostic.Summary });
}

Task WriteResults() => File.WriteAllTextAsync(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(new
{
    generatedAtUtc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(), cpuCount = Environment.ProcessorCount,
    root = runRoot, cache = "OS cache uncontrolled; first query is new-runtime/object-cold, not physical-disk-cold",
    workload = "One source; linear version/checkpoint/branch history; four valid facts per distinct pack; individual commits after batch seeding; metadata-only payload fixture; no backup compression or actual restore",
    allocation = "Process-wide managed allocation delta, including async work and instrumentation; no forced GC", rows
}, jsonOptions));

// The source corpus is never opened through a repository/runtime: even recovery initialization can write.
sealed class ReplayCorpus
{
    private readonly string _repositoryRoot;
    private readonly ReplayInputFile _descriptor;
    private readonly ReplayInputFile _catalog;
    private readonly ReplayInputFile _fixture;

    private ReplayCorpus(string repositoryRoot, HistoryConfigId configId, SourceId sourceId,
        ReplayInputFile descriptor, ReplayInputFile catalog, ReplayInputFile fixture,
        LocalReplicaCatalog decodedCatalog, ReplayPack[] packs)
    {
        _repositoryRoot = repositoryRoot;
        ConfigId = configId;
        SourceId = sourceId;
        _descriptor = descriptor;
        _catalog = catalog;
        _fixture = fixture;
        Catalog = decodedCatalog;
        Packs = packs;
    }

    public HistoryConfigId ConfigId { get; }
    public SourceId SourceId { get; }
    public LocalReplicaCatalog Catalog { get; }
    public ReplayPack[] Packs { get; }

    public static void RequireSeparateOutput(string outputRoot, string repositoryRoot)
    {
        // Protect the source fixture as well as repository.json, packs, and local state.
        var corpusRoot = Path.GetDirectoryName(repositoryRoot) ?? repositoryRoot;
        var relative = Path.GetRelativePath(corpusRoot, outputRoot);
        if (relative == "." || !Path.IsPathRooted(relative)
            && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Replay output must be outside the source corpus directory.");
    }

    public static async Task<ReplayCorpus> LoadAsync(string repositoryRoot, string outputRoot, int initialPacks)
    {
        var paths = new HistoryRepositoryPaths(repositoryRoot);
        var descriptorFile = await ReplayInputFile.ReadAsync(paths.DescriptorPath);
        var descriptor = HistoryRepositoryDescriptor.Parse(descriptorFile.Bytes);
        if (!descriptor.ToCanonicalBytes().AsSpan().SequenceEqual(descriptorFile.Bytes))
            throw new InvalidDataException("Replay repository descriptor is not canonical.");

        var codec = new HistoryPackCodec();
        var packs = new List<ReplayPack>();
        foreach (var path in Directory.EnumerateFiles(paths.PacksRoot, "*.frpack", SearchOption.AllDirectories))
        {
            var file = await ReplayInputFile.ReadAsync(path);
            var decoded = codec.Decode(file.Bytes);
            if (decoded.ContainsUnsupportedObjectSchema || !codec.Encode(decoded.Pack).AsSpan().SequenceEqual(file.Bytes))
                throw new InvalidDataException($"Replay pack does not round-trip byte-for-byte: {path}");
            if (decoded.Pack.Objects.Length != 4
                || decoded.Pack.Objects.Select(item => item.Kind).ToHashSet(StringComparer.Ordinal).SetEquals(
                    new[] { HistoryObjectKinds.SourceVersion, HistoryObjectKinds.VersionRepresentation,
                        HistoryObjectKinds.SourceCheckpoint, HistoryObjectKinds.BranchUpdate }) == false)
                throw new InvalidDataException($"Replay requires the original four-fact metadata workload: {path}");
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), paths.GetPackPath(decoded.Pack.PackId)))
                throw new InvalidDataException($"Replay pack path does not match its identity: {path}");
            var version = (SourceVersion)codec.DeserializeKnown(decoded.Pack.Objects.Single(item => item.Kind == HistoryObjectKinds.SourceVersion));
            var representation = (VersionRepresentation)codec.DeserializeKnown(decoded.Pack.Objects.Single(item => item.Kind == HistoryObjectKinds.VersionRepresentation));
            // Retain raw inputs for byte equality, without keeping the decoded seed object graph alive.
            packs.Add(new(file, decoded.Pack.PackId, decoded.Pack.CreatedAtUtc, version.SourceId, representation.RepresentationId));
        }
        var ordered = packs.OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.PackId.ToString(), StringComparer.Ordinal).ToArray();
        if (ordered.Length != initialPacks + 5 || ordered.Select(item => item.PackId).Distinct().Count() != ordered.Length)
            throw new InvalidDataException("Replay corpus must contain exactly initialPacks + 5 distinct packs.");
        if (ordered.Select(item => item.SourceId).Distinct().Count() != 1)
            throw new InvalidDataException("Replay corpus must contain exactly one source.");

        var catalogFile = await ReplayInputFile.ReadAsync(Path.Combine(paths.LocalStateRoot, "replicas.json"));
        var copiedCatalogPath = Path.Combine(outputRoot, "corpus-inputs", "replicas.json");
        Directory.CreateDirectory(Path.GetDirectoryName(copiedCatalogPath)!);
        await File.WriteAllBytesAsync(copiedCatalogPath, catalogFile.Bytes);
        LocalReplicaCatalog catalog;
        using (var store = new LocalReplicaCatalogStore(descriptor.ConfigId, copiedCatalogPath))
        {
            var loaded = await store.LoadAsync();
            catalog = loaded.Status == DeviceLocalStateStatus.Valid && loaded.Value is { } value
                ? value : throw new InvalidDataException($"Replay catalog is invalid: {loaded.Status}: {loaded.Diagnostic}");
        }
        var representations = ordered.Select(item => item.RepresentationId).ToHashSet();
        if (catalog.CatalogRevision != 1 || catalog.Entries.Length != ordered.Length
            || !catalog.Entries.Select(item => item.RepresentationId).ToHashSet().SetEquals(representations))
            throw new InvalidDataException("Replay requires the complete original catalog after its initial and final saves.");
        var fixturePath = Path.Combine(Path.GetDirectoryName(repositoryRoot)!, "metadata-only-payload.fixture");
        if (catalog.Entries.Any(item => item.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath
            || !StringComparer.OrdinalIgnoreCase.Equals(item.Locator.AbsolutePath, fixturePath)))
            throw new InvalidDataException("Replay catalog must point only to its existing metadata-only fixture.");
        var fixture = await ReplayInputFile.ReadAsync(fixturePath);
        var replay = new ReplayCorpus(repositoryRoot, descriptor.ConfigId, ordered[0].SourceId,
            descriptorFile, catalogFile, fixture, catalog, ordered);
        await replay.WriteInputEvidenceAsync(Path.Combine(outputRoot, "corpus-inputs", "manifest.json"));
        return replay;
    }

    public List<LocalReplicaCatalogEntry> GetSeedCatalog(int initialPacks)
    {
        var representations = Packs.Take(initialPacks).Select(item => item.RepresentationId).ToHashSet();
        var seed = Catalog.Entries.Where(item => representations.Contains(item.RepresentationId)).ToList();
        if (seed.Count != initialPacks)
            throw new InvalidDataException("Replay seed catalog does not cover exactly the seed representations.");
        return seed;
    }

    public void VerifyInstalledPacks(IReadOnlyList<HistoryPackReadResult> installed)
    {
        var byId = installed.ToDictionary(item => item.Pack.PackId);
        if (byId.Count != Packs.Length || Packs.Any(item => !byId.TryGetValue(item.PackId, out var actual)
            || !actual.OriginalBytes.AsSpan().SequenceEqual(item.File.Bytes)))
            throw new InvalidDataException("Replayed repository packs differ from the original corpus bytes.");
    }

    public async Task VerifyAndWriteEvidenceAsync(HistoryRepositoryPaths outputPaths, string runRoot)
    {
        var sourcePackFiles = Directory.EnumerateFiles(Path.Combine(_repositoryRoot, "packs"), "*.frpack", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!sourcePackFiles.SetEquals(Packs.Select(item => item.File.Path)))
            throw new InvalidDataException("Source corpus pack inventory changed during replay.");
        var descriptor = await VerifyFileAsync(_descriptor, outputPaths.DescriptorPath);
        var catalog = await VerifyFileAsync(_catalog, Path.Combine(outputPaths.LocalStateRoot, "replicas.json"));
        var fixture = await VerifyFileAsync(_fixture, null);
        var packEvidence = new List<object>();
        foreach (var item in Packs)
        {
            var evidence = await VerifyFileAsync(item.File, outputPaths.GetPackPath(item.PackId));
            packEvidence.Add(new { packId = item.PackId.ToString(), item.CreatedAtUtc,
                encodeDecodedMatchesInput = true, file = evidence });
        }
        await File.WriteAllTextAsync(Path.Combine(runRoot, "replay-integrity.json"), JsonSerializer.Serialize(new
        {
            sourceRepository = _repositoryRoot, outputRepository = outputPaths.RepositoryRoot,
            inputOrder = "CreatedAtUtc ascending, then PackId ordinal", initialPacks = Packs.Length - 5,
            measuredCommits = 5, validation = "Production ImportAsync/CommitAsync validate the complete existing + incoming union on every append",
            sourceInputsUnchanged = true, replayedPackBytesMatch = true, replayedDescriptorBytesMatch = true,
            replayedCatalogBytesMatch = true, payload = "Original absolute metadata-only fixture retained; no compression or restore",
            orderedPackHashesSha256 = OrderedPackHashesSha256(), descriptor, catalog, fixture, packs = packEvidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Task WriteInputEvidenceAsync(string path)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            sourceRepository = _repositoryRoot, inputOrder = "CreatedAtUtc ascending, then PackId ordinal",
            orderedPackHashesSha256 = OrderedPackHashesSha256(),
            descriptor = _descriptor.Fingerprint(), catalog = _catalog.Fingerprint(), fixture = _fixture.Fingerprint(),
            packs = Packs.Select(item => new { packId = item.PackId.ToString(), item.CreatedAtUtc,
                encodeDecodedMatchesInput = true, file = item.File.Fingerprint() }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));

    private string OrderedPackHashesSha256()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            Packs.Select(item => item.PackId + " " + item.File.Sha256))))).ToLowerInvariant();

    private static async Task<object> VerifyFileAsync(ReplayInputFile input, string? outputPath)
    {
        var after = await ReplayInputFile.ReadAsync(input.Path);
        if (!after.Bytes.AsSpan().SequenceEqual(input.Bytes))
            throw new InvalidDataException($"Source input changed during replay: {input.Path}");
        var output = outputPath is null ? null : await ReplayInputFile.ReadAsync(outputPath);
        if (output is not null && !output.Bytes.AsSpan().SequenceEqual(input.Bytes))
            throw new InvalidDataException($"Replayed file differs from its input: {outputPath}");
        return new { path = input.Path, bytes = input.Bytes.Length, sha256Before = input.Sha256,
            sha256After = after.Sha256, sourceUnchanged = true, outputPath, outputSha256 = output?.Sha256,
            outputMatchesInput = output is null ? (bool?)null : true };
    }
}

sealed record ReplayPack(ReplayInputFile File, PackId PackId, DateTimeOffset CreatedAtUtc, SourceId SourceId, RepresentationId RepresentationId);

sealed record ReplayInputFile(string Path, byte[] Bytes, string Sha256)
{
    public static async Task<ReplayInputFile> ReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return new(path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public object Fingerprint() => new { path = Path, bytes = Bytes.Length, sha256 = Sha256 };
}
