using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System.Globalization;
using System.Text.Json;

if (args.Length != 3 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var depth)
    || depth is < 2 or > 100000 || args[1] is not ("representations" or "partial") || !Path.IsPathFullyQualified(args[2]))
    throw new ArgumentException("Usage: HistoryGraphProbe <depth> <representations|partial> <absolute-fixture-root>");

var partial = args[1] == "partial";
var root = Path.GetFullPath(args[2]);
Directory.CreateDirectory(root);
if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Probe fixture directory must be empty.");
var config = new HistoryConfigId("graph-probe-" + Guid.NewGuid().ToString("N"));
var source = SourceId.New();
var codec = new HistoryPackCodec();
var paths = new HistoryRepositoryPaths(Path.Combine(root, "repository"));
var versions = new List<SourceVersion>();
var representations = new List<VersionRepresentation>();
var entries = new List<LocalReplicaCatalogEntry>();
var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var payloadRoot = Path.Combine(root, "payloads");
Directory.CreateDirectory(payloadRoot);
for (int i = 0; i < depth; i++)
{
    var version = new SourceVersion(VersionId.New(), config, source,
        i == 0 ? [] : new[] { versions[^1].VersionId }, time.AddSeconds(i), null,
        partial && i > 0 ? CaptureScope.PartialSource : CaptureScope.FullSource, CaptureOutcome.Captured, [],
        new("deep history", root), null, HistoryProvenance.Native("graph-probe"));
    // Presentation checks existence and immutable metadata, never archive contents.
    // Every dependency has real bytes, so neither availability nor size can short-circuit.
    var payload = Path.Combine(payloadRoot, i.ToString(CultureInfo.InvariantCulture) + ".fixture");
    File.WriteAllBytes(payload, [42]);
    var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId,
        partial || i == 0 ? RepresentationKind.CoreFull : RepresentationKind.CoreSmartDelta, "7z",
        partial || i == 0 ? [] : new[] { representations[^1].RepresentationId },
        MaterializationFidelity.Exact, null, null,
        new Dictionary<string, string> { [BackupSizeMetadata.StoredBytesKey] = new FileInfo(payload).Length.ToString(CultureInfo.InvariantCulture) });
    versions.Add(version);
    representations.Add(representation);
    entries.Add(new(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload), time));
}
var latest = versions[^1];
var checkpoint = new SourceCheckpoint(CheckpointId.New(), config, latest.CreatedAtUtc, null,
    HistoryProvenance.Native("graph-probe"),
    [new(source, latest.SourceDescriptorSnapshot, latest.VersionId, CheckpointSourceDisposition.Captured)]);
// Only the deepest Version has a checkpoint. This forces a cold full-depth traversal
// regardless of SQLite ordering, without quadratic all-root size projections in setup.
var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), time,
    versions.AsEnumerable().Reverse().Cast<object>().Concat(representations.AsEnumerable().Reverse())
        .Append(checkpoint).Select(item => codec.CreateObject(item)));
await using (var seed = new HistoryRuntime(new FileHistoryRepository(config, paths)))
{
    await seed.InitializeAsync();
    await seed.Repository.CommitAsync(pack);
    var existing = await seed.LocalReplicaCatalogStore.LoadAsync();
    await seed.LocalReplicaCatalogStore.SaveAsync(new(config, (existing.Value?.CatalogRevision ?? -1) + 1, entries),
        existing.Value?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision);
}

await using var runtime = new HistoryRuntime(new FileHistoryRepository(config, paths));
await runtime.InitializeAsync();
var stored = await runtime.Repository.ReadAllPacksAsync();
new HistoryRepositoryValidator(codec).Validate(config, stored);
Stage("VALIDATED");
var admission = await new HistoryExactCheckpointAdmission(runtime).EvaluateAsync(checkpoint);
if (!admission.IsReady) throw new InvalidDataException("Admission failed: " + admission.Diagnostic);
Stage("ADMITTED");
var query = new HistoryPresentationQueryService(runtime);
var snapshot = await query.QueryAsync(source);
Check(snapshot);
Stage("QUERIED");
var page = await query.QueryPageAsync(new(source, false, true, null, "", 100));
Check(page.Snapshot);
if (page.TotalCount != 1 || page.Next is not null) throw new InvalidDataException("Unexpected page boundaries.");
Stage("PAGED");
Console.WriteLine(JsonSerializer.Serialize(new
{
    depth, scenario = args[1], status = admission.Status.ToString(),
    readiness = snapshot.Timeline.Single().Readiness.ToString(),
    dependencyBytes = snapshot.Timeline.Single().SizeStatistics!.RestoreDependencyBytes,
    rows = page.TotalCount,
    coreAssembly = typeof(HistoryRuntime).Assembly.GetName().Name,
    probeAssembly = typeof(Program).Assembly.GetName().Name
}));

void Check(HistoryPresentationSnapshot result)
{
    var row = result.Timeline.Single();
    if (row.VersionId != latest.VersionId || row.RepresentationId != representations[^1].RepresentationId
        || row.BranchableCheckpointId != checkpoint.CheckpointId || row.BranchableCheckpointCount != 1
        || row.Readiness != HistoryPresentationReadiness.Ready || row.Fidelity != MaterializationFidelity.Exact
        || row.SizeStatistics?.RestoreDependencyBytes != (partial ? 1L : depth))
        throw new InvalidDataException("Deep history projection changed admission, availability, selection or size semantics.");
}

void Stage(string stage)
{
    Console.WriteLine($"{stage}: depth={depth}, scenario={args[1]}");
    Console.Out.Flush();
}
