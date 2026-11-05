using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderRewind.MergePerformanceProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();
    private static readonly string[] StatusNames = ["Automatic", "Resolved", "Unresolved", "Locked", "Blocked"];
    private static readonly MergeDecisionKind[] SideChoices = [MergeDecisionKind.Ours, MergeDecisionKind.Theirs, MergeDecisionKind.Base, MergeDecisionKind.Delete];
    private static int _cancelImportMs;

    internal static async Task<int> Main(string[] args)
    {
        var output = Path.GetFullPath(Get(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "results"));
        var counts = (Get(args, "--counts") ?? "1000,10000,20000").Split(',').Select(int.Parse).ToArray();
        var batch = int.Parse(Get(args, "--batch") ?? "500");
        var planBytes = int.Parse(Get(args, "--plan-bytes-per-region") ?? "0");
        _cancelImportMs = int.Parse(Get(args, "--cancel-import-ms") ?? "0");
        if (counts.Any(n => n < 100 || n > 100000) || batch < 1 || batch > 500 || planBytes < 0 || planBytes > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(output);
        var run = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(run);
        var metadata = new
        {
            Utc = DateTimeOffset.UtcNow, Environment.Version, Environment.OSVersion, Environment.ProcessorCount,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Counts = counts, Batch = batch, PlanBytesPerRegion = planBytes,
            Method = "Real Core SemanticMergePlanWriter / MergeSessionStore and Host index; fixed emitted changes. Candidate uses production single-snapshot builder, baseline uses original display paging. No archives, Analyze or XAML rendering.",
#if MERGE_SNAPSHOT
            ImportMode = "single-snapshot",
#else
            ImportMode = "display-pages",
#endif
            SubjectMix = "95% automatic target-side retention, 4% automatic source-side adoption, 1% unresolved. Fully unchanged raw chunks are not emitted by the actual Minecraft provider. N counts emitted subjects, not world chunks.",
            Timing = "One 100-subject warmup precedes ascending sizes. Process-wide managed allocation/cpu deltas. No forced GC, no OS cache flush. Subject construction is a separate stage. Database files retained under run directory."
        };
        await File.WriteAllTextAsync(Path.Combine(run, "metadata.json"), JsonSerializer.Serialize(metadata, Json));
        await RunCaseAsync(Path.Combine(run, "warmup"), 100, batch, planBytes, false);
        foreach (var count in counts)
        {
            var result = await RunCaseAsync(Path.Combine(run, "n" + count), count, batch, planBytes, true);
            await File.WriteAllTextAsync(Path.Combine(run, "n" + count + ".json"), JsonSerializer.Serialize(result, Json));
            Console.WriteLine(JsonSerializer.Serialize(new { count, result.WriterTotal.ElapsedMs, result.ImportTotal, result.FirstScreen, result.TotalSubjects, result.DefaultUnresolvedRows }));
        }
        Console.WriteLine("RESULT_DIRECTORY=" + run);
        return 0;
    }

    private static string? Get(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name);
    }

    private static async Task<CaseResult> RunCaseAsync(string root, int count, int batchSize, int planBytes, bool retain)
    {
        Directory.CreateDirectory(root);
        var construction = Begin();
        var fixture = MakeFixture(root, count);
        var generated = End(construction);
        var result = new CaseResult { RequestedSubjects = count, BatchSize = batchSize, Groups = fixture.Groups.Length, SubjectConstruction = generated };
        var store = new MergeSessionStore(Path.Combine(root, "local-state"));
        var create = Begin();
        var session = store.Create(fixture.Plan, fixture.Versions);
        result.SessionCreate = End(create);
        var allWrites = Begin();
        var writerCreate = Begin();
        using (var writer = new SemanticMergePlanWriter(store, session, fixture.Source, fixture.Tree, fixture.Tree, fixture.Tree, _ => true, default))
        {
            result.WriterCreate = End(writerCreate);
            foreach (var group in fixture.Groups)
            {
                var phase = Begin();
                await writer.AddGroupAsync(group.Group, default);
                result.AddGroup = Add(result.AddGroup, End(phase));
                phase = Begin();
                await writer.ClaimPathsAsync(group.Group.GroupId, [group.Path], [group.Path], default);
                result.ClaimPaths = Add(result.ClaimPaths, End(phase));
                result.PathBatches++;
                if (planBytes > 0)
                {
                    phase = Begin();
                    await using (var stream = await writer.OpenDataWriteAsync(group.IndexKey, default))
                    {
                        // A controlled plan-data sensitivity variant; not a Minecraft CoordinatePlan emulation.
                        var data = new byte[planBytes];
                        await stream.WriteAsync(data);
                    }
                    result.WriteAndSealPlanData = Add(result.WriteAndSealPlanData, End(phase));
                    result.PlanDataBytes += planBytes;
                    result.PlanDataRecords++;
                }
                foreach (var subjects in group.Subjects.Chunk(batchSize))
                {
                    phase = Begin();
                    await writer.AddSubjectsAsync(subjects, default);
                    result.AddSubjects = Add(result.AddSubjects, End(phase));
                    result.SubjectBatches++;
                }
            }
            var complete = Begin();
            var owned = await writer.CompleteAsync();
            result.WriterComplete = End(complete);
            Require(owned.Count == fixture.Groups.Length, "Unexpected path ownership count.");
        }
        var save = Begin();
        store.SaveSource(session, new(fixture.Plan.Sources.Single(), MergeTreeManifest.Empty, fixture.Tree, fixture.Tree, fixture.Tree), [], retainSemantic: true);
        result.SaveSource = End(save);
        var completePreparation = Begin();
        session = store.CompletePreparation(session, fixture.Versions);
        result.CompletePreparation = End(completePreparation);
        result.WriterTotal = End(allWrites);
        result.ExpectedWriteTransactions = 1 + fixture.Groups.Length + result.PathBatches + result.SubjectBatches + result.PlanDataRecords + 1 + 1;
        Checkpoint(root, result, "writer-complete");
        var firstQuery = Begin();
        var first = store.QuerySemanticChanges(session, "", "All", 0, 100);
        result.StoreFirstPage = End(firstQuery);
        result.TotalSubjects = first.Total;
        result.AllMatchingRows = first.Matching;
        result.AllFirstPageRows = first.Rows.Count;
        var unresolvedQuery = Begin();
        var unresolved = store.QuerySemanticChanges(session, "", "Unresolved", 0, 100);
        result.StoreDefaultUnresolvedPage = End(unresolvedQuery);
        result.DefaultUnresolvedRows = unresolved.Matching;
        result.DefaultUnresolvedFirstPageRows = unresolved.Rows.Count;
        Require(first.Total == count && first.Matching == count && unresolved.Matching == fixture.Unresolved, "Subject/filter counts differ from generated inputs.");
        Checkpoint(root, result, "store-first-pages-complete");

        // Both branches time export/read, all index writes and the final revision check.
        // Candidate ownership is published by assigning its complete index, never by mutating an old index.
        var import = Begin();
#if MERGE_SNAPSHOT
        using var cancellation = new CancellationTokenSource();
        if (retain && _cancelImportMs > 0) cancellation.CancelAfter(_cancelImportMs);
        SpatialMergeIndexBuild completed;
        try
        {
            completed = SpatialMergeIndexBuilder.Build(store, session, Path.Combine(root, "private"),
                item => ToRow((item.Conflict, item.Resolution, item.Automatic)), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result.ImportTotal = End(import); result.ImportCanceled = true;
            result.CancellationDrainMs = Math.Max(0, result.ImportTotal.ElapsedMs - _cancelImportMs);
            Require(!Directory.Exists(Path.Combine(root, "private")) || !Directory.EnumerateFileSystemEntries(Path.Combine(root, "private")).Any(),
                "Canceled snapshot left a partial candidate index.");
            Checkpoint(root, result, "index-import-canceled");
            return result;
        }
        using var build = completed;
        var index = build.Index;
#else
        using var index = new SpatialWorkspaceIndex(Path.Combine(root, "spatial.db"));
        index.Clear();
        for (var offset = 0; ; offset += 1000)
        {
                var readPhase = Begin();
                var page = store.QuerySemanticChanges(session, "", "All", offset, 1000);
                var read = End(readPhase);
                var putPhase = Begin();
                index.Put(page.Rows.Select(ToRow));
                var put = End(putPhase);
                result.Pages.Add(new(offset, page.Rows.Count, read, put));
                result.ImportRead = Add(result.ImportRead, read);
                result.ImportPut = Add(result.ImportPut, put);
                if (page.Rows.Count < 1000) break;
        }
#endif
        var current = store.Load(session.Id);
        Require(current.Revision == session.Revision && current.Plan.Revision == session.Plan.Revision && current.State == session.State,
            "Cannot publish a spatial index for a changed session.");
        result.ImportTotal = End(import);
        CurrentProcess.Refresh(); result.PeakWorkingSetBytes = CurrentProcess.PeakWorkingSet64;
        {
            Checkpoint(root, result, "index-import-complete");
            var query = new SpatialWorkspaceQuery(".", "minecraft:overworld", Status: "Unresolved", Group: false);
            var screenPhase = Begin();
            var rows = index.Query(query, 0, 101);
            var visibleCount = index.Count(query);
            var counts = index.Counts();
            var categories = index.CategoryCounts(query);
            var whole = index.WholeDomainCount(query);
            var withoutCategories = index.Count(query with { Categories = null });
            var statusCounts = StatusNames.Select(status => index.Count(query with { Status = status })).ToArray();
            result.FirstScreen = End(screenPhase);
            result.SpatialFirstScreenRows = Math.Min(100, rows.Count);
            result.SpatialMatchingRows = visibleCount;
            result.SpatialTotalRows = counts.Total;
            result.SpatialNonSpatialRows = counts.NonSpatial;
            result.SpatialWholeDomainRows = whole;
            result.SpatialStatusCounts = StatusNames.Zip(statusCounts).ToDictionary(p => p.First, p => p.Second);
            Require(visibleCount == unresolved.Matching && counts.Total == count && categories.Count == 0 && whole == 0 && withoutCategories == visibleCount, "Host index projection lost rows.");
            Checkpoint(root, result, "first-screen-complete");
            var overlayPhase = Begin();
            result.OverlayCount = index.Overlays(query, new(-16, -16, 512, 512), .1, 16).Count;
            result.FirstViewportOverlay = End(overlayPhase);
        }
        result.Files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(p => new FileResult(Path.GetRelativePath(root, p), new FileInfo(p).Length)).ToArray();
        if (!retain) { /* Warmup artifacts are deliberately retained; no filesystem cleanup is timed. */ }
        return result;
    }

    private static SpatialWorkspaceRow ToRow((MergeConflict Conflict, MergeResolution? Resolution, bool Automatic) item)
    {
        var subject = item.Conflict.SemanticSubject ?? throw new InvalidDataException();
        var status = subject.IsBlocked ? "Blocked" : subject is { IsAutomatic: true, CanOverrideAutomatic: false } ? "Locked"
            : item.Resolution is null ? "Unresolved" : item.Resolution.Choice == MergeResolutionChoice.Automatic ? "Automatic" : "Resolved";
        return new(item.Conflict.Id, subject.DisplayName.Default, subject.Description.Default + "\n" + (item.Automatic ? "Automatic" : "Conflict"), status,
            (!item.Automatic || subject.CanOverrideAutomatic) && !subject.IsBlocked, subject.SpatialLocations);
    }

    private static Fixture MakeFixture(string root, int count)
    {
        var source = SourceId.New(); var version = VersionId.New(); var checkpoint = CheckpointId.New();
        var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
        var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
        var identity = new CheckpointSource(source, new("world", Path.Combine(root, "world")), version, CheckpointSourceDisposition.Captured);
        var binding = new SemanticMergeBinding(new(new("com.test.merge-performance"), "chunks"), 1, "1.0.0", new string('A', 64), "conservative", "1", new(new("folderrewind.core"), "default"));
        var descriptor = binding.Descriptor;
        var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, ours, theirs, checkpoint,
            new(new HistoryConfigId("merge-performance"), 0, Array.Empty<WorkspaceSourceBaseline>()), "performance", [],
            [new(source, identity, identity, identity, HistoryMergeSourceAction.MergeFiles, null)], descriptor.Identity, descriptor.PolicyIdentity) { SemanticProvider = binding };
        var groups = new List<GroupInput>();
        var files = new Dictionary<string, MergeFileValue>();
        var unresolved = 0;
        for (var start = 0; start < count; start += 1024)
        {
            var region = start / 1024; var key = Hash("region" + region); var groupId = "region/" + key; var indexKey = "coordinates/" + key;
            var path = $"region/r.{region}.0.mca";
            files.Add(path, new(Path.Combine(root, "synthetic-unopened-input", path), Hash(path), 4096));
            var subjects = new List<MergeSubject>();
            for (var ordinal = start; ordinal < Math.Min(count, start + 1024); ordinal++)
            {
                var slot = ordinal - start; var x = region * 32 + slot % 32; var z = slot / 32;
                var automaticSide = ordinal % 100 == 99 ? -1 : ordinal % 100 >= 95 ? 2 : 1;
                if (automaticSide < 0) unresolved++;
                subjects.Add(new(slot.ToString(), groupId, Hash("input:" + ordinal), Text($"Chunk ({x}, {z})", $"区块（{x}, {z}）"),
                    Text(automaticSide == 2 ? "Only the source changed content; use its complete chunk, including matching caches and runtime state." : "Terrain, Entities and POI move together at this coordinate.",
                        automaticSide == 2 ? "只有来源改变内容；采用来源完整区块及其配套缓存、运行状态。" : "此坐标的 Terrain、Entities 与 POI 统一选择。"),
                    SideChoices, automaticSide >= 0, JsonSerializer.SerializeToElement(new { Slot = slot, AutomaticSide = automaticSide }))
                { CanOverrideAutomatic = automaticSide >= 0, SpatialLocations = [new(".", "minecraft:overworld", new(x * 16d, z * 16d, (x + 1d) * 16, (z + 1d) * 16))] });
            }
            var group = new MergeOutputGroup(groupId, Text($". minecraft:overworld ({region}, 0)", $". minecraft:overworld（{region}, 0）"),
                JsonSerializer.SerializeToElement(new { IndexKey = indexKey, WholeWorld = false, TerrainFirst = false, IsBlocked = false }));
            groups.Add(new(group, path, indexKey, subjects.ToArray()));
        }
        var tree = MergeTreeManifest.Create(files) with { InputRepresentationId = RepresentationId.New(), HasCompleteDirectoryInventory = true };
        return new(source, [version], plan, tree, groups.ToArray(), unresolved);
    }

    private static LocalizedText Text(string english, string chinese) => new(english, new Dictionary<string, string> { ["en-US"] = english, ["zh-CN"] = chinese });
    private static void Checkpoint(string root, CaseResult result, string stage)
    {
        File.WriteAllText(Path.Combine(root, "partial.json"), JsonSerializer.Serialize(new { Stage = stage, Utc = DateTimeOffset.UtcNow, Result = result }, Json));
        Console.WriteLine($"HOST_PHASE count={result.RequestedSubjects} stage={stage}");
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static Stamp Begin() => new(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(precise: true), CurrentProcess.TotalProcessorTime.TotalMilliseconds, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    private static Measurement End(Stamp stamp) => new(Stopwatch.GetElapsedTime(stamp.Ticks).TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - stamp.AllocatedBytes,
        CurrentProcess.TotalProcessorTime.TotalMilliseconds - stamp.CpuMs, GC.CollectionCount(0) - stamp.Gen0, GC.CollectionCount(1) - stamp.Gen1, GC.CollectionCount(2) - stamp.Gen2);
    private static Measurement Add(Measurement a, Measurement b) => new(a.ElapsedMs + b.ElapsedMs, a.AllocatedBytes + b.AllocatedBytes, a.CpuMs + b.CpuMs, a.Gen0 + b.Gen0, a.Gen1 + b.Gen1, a.Gen2 + b.Gen2);
    private sealed record Stamp(long Ticks, long AllocatedBytes, double CpuMs, int Gen0, int Gen1, int Gen2);
    internal sealed record Measurement(double ElapsedMs = 0, long AllocatedBytes = 0, double CpuMs = 0, int Gen0 = 0, int Gen1 = 0, int Gen2 = 0);
    private sealed record GroupInput(MergeOutputGroup Group, string Path, string IndexKey, MergeSubject[] Subjects);
    private sealed record Fixture(SourceId Source, VersionId[] Versions, HistoryMergePlan Plan, MergeTreeManifest Tree, GroupInput[] Groups, int Unresolved);
    internal sealed record PageResult(int Offset, int Rows, Measurement StoreRead, Measurement IndexPut);
    internal sealed record FileResult(string Path, long Bytes);
    internal sealed class CaseResult
    {
        public int RequestedSubjects { get; set; }
        public int BatchSize { get; set; }
        public int Groups { get; set; }
        public int SubjectBatches { get; set; }
        public int PathBatches { get; set; }
        public int PlanDataRecords { get; set; }
        public long PlanDataBytes { get; set; }
        public int ExpectedWriteTransactions { get; set; }
        public Measurement SubjectConstruction { get; set; } = new();
        public Measurement SessionCreate { get; set; } = new();
        public Measurement WriterCreate { get; set; } = new();
        public Measurement AddGroup { get; set; } = new();
        public Measurement ClaimPaths { get; set; } = new();
        public Measurement AddSubjects { get; set; } = new();
        public Measurement WriteAndSealPlanData { get; set; } = new();
        public Measurement WriterComplete { get; set; } = new();
        public Measurement SaveSource { get; set; } = new();
        public Measurement CompletePreparation { get; set; } = new();
        public Measurement WriterTotal { get; set; } = new();
        public Measurement StoreFirstPage { get; set; } = new();
        public Measurement StoreDefaultUnresolvedPage { get; set; } = new();
        public Measurement ImportTotal { get; set; } = new();
        public Measurement ImportRead { get; set; } = new();
        public Measurement ImportPut { get; set; } = new();
        public Measurement FirstScreen { get; set; } = new();
        public Measurement FirstViewportOverlay { get; set; } = new();
        public long PeakWorkingSetBytes { get; set; }
        public bool ImportCanceled { get; set; }
        public double? CancellationDrainMs { get; set; }
        public int TotalSubjects { get; set; }
        public int AllMatchingRows { get; set; }
        public int AllFirstPageRows { get; set; }
        public int DefaultUnresolvedRows { get; set; }
        public int DefaultUnresolvedFirstPageRows { get; set; }
        public int SpatialFirstScreenRows { get; set; }
        public int SpatialMatchingRows { get; set; }
        public int SpatialTotalRows { get; set; }
        public int SpatialNonSpatialRows { get; set; }
        public int SpatialWholeDomainRows { get; set; }
        public int OverlayCount { get; set; }
        public Dictionary<string, int> SpatialStatusCounts { get; set; } = [];
        public List<PageResult> Pages { get; set; } = [];
        public FileResult[] Files { get; set; } = [];
    }
}
