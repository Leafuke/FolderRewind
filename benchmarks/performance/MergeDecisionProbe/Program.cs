using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using FolderRewind.Tests;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var json = new JsonSerializerOptions { WriteIndented = true };
string? Get(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : args[index + 1]; }
var count = int.Parse(Get("--rows") ?? "20000");
var operations = int.Parse(Get("--operations") ?? "10");
var rounds = int.Parse(Get("--rounds") ?? "3");
if (count is < 16 or > 100000 || operations is < 3 or > 100 || rounds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(args));
var output = Path.GetFullPath(Get("--output") ?? "artifacts/merge-decision-incremental-20261105/results");
var run = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(run);
var sourceRoot = Path.GetFullPath(Get("--source-root") ?? Environment.CurrentDirectory);
var paths = new[]
{
    "src/FolderRewind.Core/History/LocalState/MergeSessionStore.Snapshot.cs",
    "src/FolderRewind.Core/History/Merge/MergeDecisionChange.cs",
    "src/FolderRewind/Services/History/SpatialWorkspaceIndex.cs",
    "src/FolderRewind/Services/History/SpatialWorkspaceIndex.Presentation.cs",
    "src/FolderRewind/Services/History/SpatialWorkspaceIndex.Snapshot.cs",
    "src/FolderRewind/Services/History/SpatialIndexDiagnostics.cs",
    "src/FolderRewind/Services/History/SpatialMergeIndexBuilder.cs",
    "src/FolderRewind/Services/History/SpatialMergeIndexUpdater.cs",
    "src/FolderRewind/Services/History/SpatialMergeRows.cs",
    "tests/FolderRewind.Tests/History/MergeDecisionIndexFixture.cs",
    "benchmarks/performance/MergeDecisionProbe/Program.cs",
    "benchmarks/performance/MergeDecisionProbe/MergeDecisionProbe.csproj"
};
File.WriteAllText(Path.Combine(run, "metadata.json"), JsonSerializer.Serialize(new
{
    Utc = DateTimeOffset.UtcNow, Rows = count, Operations = operations, Rounds = rounds,
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Environment.ProcessorCount,
    CoreAssembly = typeof(MergeSessionStore).Assembly.Location,
    CoreSha256 = Hash(File.ReadAllBytes(typeof(MergeSessionStore).Assembly.Location)),
    ProbeSha256 = Hash(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location)),
    Sources = paths.Select(path => new { Path = path, Sha256 = Hash(File.ReadAllBytes(Path.Combine(sourceRoot, path))) }),
    Method = "Real formal Core store, production updater and production full builder on the same post-persistence snapshot. Each operation changes one ID; pair order alternates. Persistence, fixture creation, initial import, selection setup and correctness checks are outside both timed regions.",
    Corpus = "Synthetic already-emitted subjects, mostly overridable automatic results plus unresolved, locked, blocked, whole-domain and nonspatial Host display controls. Cycles exercise resolve/clear/undo-clear on unresolved and automatic subjects.",
    Limits = "Synthetic emitted change metadata only; no plugin Analyze, archives, UI dispatch, preview tiles or Build/Apply. Warmup precedes serial rounds; no cache flush or forced GC. Managed allocations and CPU are process-wide interval deltas. Temporary corpus/index directories are removed after each round."
}, json));
using (var warmup = new MergeDecisionIndexFixture(16))
{
    using var index = warmup.Build(); var change = warmup.Resolve(0, MergeResolutionChoice.Ours);
    SpatialMergeIndexUpdater.Apply(warmup.Store, index, change, MergeDecisionIndexFixture.Project);
    using var rebuilt = warmup.Build();
}
var commandProof = new List<object>();
foreach (var proofCount in new[] { 16, 1024 })
{
    using var fixture = new MergeDecisionIndexFixture(proofCount); var diagnostics = new SpatialIndexDiagnostics();
    using var published = fixture.BuildMetered(diagnostics); var change = fixture.Resolve(0, MergeResolutionChoice.Ours); var projected = 0;
    SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, record => { projected++; return MergeDecisionIndexFixture.Project(record); });
    var sample = diagnostics.Operations.Last(operation => operation.Kind == "merge-update");
    Require(projected == 1 && sample.Commands <= 8 && sample.EndVersion == sample.BeginVersion + 1, "Single-row update command scope changed.");
    commandProof.Add(new { TotalRows = proofCount, ProjectedRows = projected, sample.Commands, sample.BeginVersion, sample.EndVersion,
        Scope = "Opt-in Host index command collector; Core read uses requested-ID snapshot API plus fresh session validation. Initialization and this proof are outside timed pairs." });
}
File.WriteAllText(Path.Combine(run, "bounded-command-proof.json"), JsonSerializer.Serialize(commandProof, json));
var all = new List<Step>();
for (var round = 0; round < rounds; round++)
{
    using var fixture = new MergeDecisionIndexFixture(count, Path.Combine(run, "corpus-" + round));
    using var published = fixture.Build();
    var originalIndex = published.Index; var originalDirectory = published.DirectoryPath;
    var rows = new List<Step>();
    MergeResolution? undo = null;
    for (var operation = 0; operation < operations; operation++)
    {
        var ordinal = operation / 3 % 2;
        var conflict = fixture.Conflicts[ordinal];
        var persistence = Stopwatch.StartNew();
        MergeDecisionChange change; string kind;
        if (operation % 3 == 0)
        {
            change = fixture.Resolve(ordinal, MergeResolutionChoice.Theirs); kind = "resolve";
        }
        else if (operation % 3 == 1)
        {
            undo = fixture.Store.GetConflict(fixture.Session, conflict.Id).Resolution;
            change = fixture.Clear(ordinal); kind = "clear";
        }
        else { change = fixture.Set(new Dictionary<string, MergeResolution?> { [conflict.Id] = undo }); kind = "undo"; }
        persistence.Stop();
        published.Index.SelectItem(fixture.Conflicts[6].Id, false);
        Measurement incremental = new(), full = new(); int projected = 0, fullProjected = 0;
        SpatialMergeIndexBuild? rebuilt = null;
        void Incremental()
        {
            var stamp = Stamp.Start();
            SpatialMergeIndexUpdater.Apply(fixture.Store, published, change, record => { projected++; return MergeDecisionIndexFixture.Project(record); });
            incremental = stamp.End();
        }
        void Full()
        {
            var stamp = Stamp.Start();
            rebuilt = SpatialMergeIndexBuilder.Build(fixture.Store, fixture.Session, Path.Combine(fixture.Root, "full"),
                record => { fullProjected++; return MergeDecisionIndexFixture.Project(record); });
            full = stamp.End();
        }
        try
        {
            if ((round + operation) % 2 == 0) { Incremental(); Full(); } else { Full(); Incremental(); }
            Require(projected == 1 && fullProjected == count, "Unexpected projected row scope.");
            Require(ReferenceEquals(originalIndex, published.Index) && originalDirectory == published.DirectoryPath, "Single-item update replaced the published database owner.");
            Require(published.Snapshot.Revision == change.After.Revision && published.Snapshot.Total == count, "Published snapshot did not advance correctly.");
            Require(published.Index.Selected().Length == 0, "Selection was not cleared atomically.");
            var changed = published.Index.ByIds([conflict.Id]); var reference = rebuilt!.Index.ByIds([conflict.Id]);
            Require(JsonSerializer.Serialize(changed) == JsonSerializer.Serialize(reference), "Changed row differs from full rebuild.");
            var sentinelId = fixture.Conflicts[count - 1].Id;
            Require(JsonSerializer.Serialize(published.Index.ByIds([sentinelId])) == JsonSerializer.Serialize(rebuilt.Index.ByIds([sentinelId])), "Untouched row differs from full rebuild.");
            string? digest = null;
            if (operation == operations - 1)
            {
                digest = Hash(Encoding.UTF8.GetBytes(MergeDecisionIndexFixture.Rows(published.Index)));
                Require(digest == Hash(Encoding.UTF8.GetBytes(MergeDecisionIndexFixture.Rows(rebuilt.Index))), "Final full-index contents differ.");
            }
            rows.Add(new(round, operation, kind, change.Before.Revision, change.After.Revision, persistence.Elapsed.TotalMilliseconds,
                (round + operation) % 2 == 0 ? "incremental-first" : "full-first", projected, fullProjected, incremental, full,
                SameOwner: true, SelectionCleared: true, FinalFullRowsSha256: digest));
            Console.WriteLine($"round={round} operation={operation} kind={kind} incrementalMs={incremental.ElapsedMs:F3} fullRebuildMs={full.ElapsedMs:F3}");
        }
        finally { rebuilt?.Dispose(); }
    }
    all.AddRange(rows);
    File.WriteAllText(Path.Combine(run, "round-" + round + ".json"), JsonSerializer.Serialize(rows, json));
}
File.WriteAllText(Path.Combine(run, "results.json"), JsonSerializer.Serialize(new
{
    Rows = count, Operations = operations, Rounds = rounds, Samples = all,
    MedianIncrementalMs = Median(all.Select(row => row.Incremental.ElapsedMs)),
    MedianFullRebuildMs = Median(all.Select(row => row.FullRebuild.ElapsedMs)),
    MedianIncrementalAllocatedBytes = Median(all.Select(row => (double)row.Incremental.AllocatedBytes)),
    MedianFullRebuildAllocatedBytes = Median(all.Select(row => (double)row.FullRebuild.AllocatedBytes))
}, json));
Console.WriteLine("RESULT_DIRECTORY=" + run);

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2]; }
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
sealed record Measurement(double ElapsedMs = 0, long AllocatedBytes = 0, double CpuMs = 0);
sealed record Step(int Round, int Operation, string Kind, long BeforeRevision, long AfterRevision, double PersistenceMsExcluded,
    string Order, int IncrementalProjectedRows, int FullProjectedRows, Measurement Incremental, Measurement FullRebuild,
    bool SameOwner, bool SelectionCleared, string? FinalFullRowsSha256);
sealed record Stamp(long Ticks, long Allocated, double CpuMs)
{
    internal static Stamp Start()
    {
        using var process = Process.GetCurrentProcess(); var allocated = GC.GetTotalAllocatedBytes(true); var cpu = process.TotalProcessorTime.TotalMilliseconds;
        return new(Stopwatch.GetTimestamp(), allocated, cpu);
    }
    internal Measurement End()
    {
        var end = Stopwatch.GetTimestamp(); var allocated = GC.GetTotalAllocatedBytes(true) - Allocated; using var process = Process.GetCurrentProcess();
        return new(Stopwatch.GetElapsedTime(Ticks, end).TotalMilliseconds, allocated, process.TotalProcessorTime.TotalMilliseconds - CpuMs);
    }
}
