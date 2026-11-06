using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

// Shared by the focused regression tests and the repeatable decision-update benchmark.
// Only metadata is seeded. Input handles are never opened and this fixture cannot Build/Apply.
internal sealed class MergeDecisionIndexFixture : IDisposable
{
    internal string Root { get; }
    internal MergeSessionStore Store { get; }
    internal MergeSession Session { get; private set; }
    internal IReadOnlyDictionary<int, MergeConflict> Conflicts { get; }
    internal MergeDecisionIndexFixture(int count, string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "fr-decision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "local"));
        var source = SourceId.New(); var version = VersionId.New(); var checkpoint = CheckpointId.New();
        var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
        var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
        var identity = new CheckpointSource(source, new("world", Path.Combine(Root, "world")), version, CheckpointSourceDisposition.Captured);
        var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, ours, theirs, checkpoint,
            new(new HistoryConfigId("decision-fixture"), 0, Array.Empty<WorkspaceSourceBaseline>()), "fixture", [],
            [new(source, identity, identity, identity, HistoryMergeSourceAction.MergeFiles, null)]);
        var session = Store.Create(plan, [version]);
        var paths = Enumerable.Range(0, (count + 1023) / 1024).Select(i => $"region/r.{i}.0.mca").ToArray();
        var tree = MergeTreeManifest.Create(paths.ToDictionary(p => p, p => new MergeFileValue(Path.Combine(Root, "unopened", p), Hash(p), 4096)));
        using (var writer = new SemanticMergePlanWriter(Store, session, source, tree, tree, tree, _ => true, default))
        {
            for (var start = 0; start < count; start += 1024)
            {
                var group = "group-" + start / 1024;
                writer.AddGroupAsync(new(group, Text(group), JsonSerializer.SerializeToElement(group)), default).GetAwaiter().GetResult();
                writer.ClaimPathsAsync(group, [paths[start / 1024]], [paths[start / 1024]], default).GetAwaiter().GetResult();
                foreach (var batch in Enumerable.Range(start, Math.Min(1024, count - start)).Where(i => i != 4).Select(i => Subject(i, group)).Chunk(500))
                    writer.AddSubjectsAsync(batch, default).GetAwaiter().GetResult();
            }
            writer.CompleteAsync().GetAwaiter().GetResult();
        }
        var generic = count > 4 ? new[] { new MergeConflict("host-display", new(source, ["generic.txt"]), MergeConflictKind.ModifyModify, "host-display",
            tree.Files.Clear(), tree.Files.Clear(), tree.Files.Clear()) { AutomaticDisplayOnly = true } } : [];
        Store.SaveSource(session, new(plan.Sources.Single(), MergeTreeManifest.Empty, tree, tree, tree), generic, retainSemantic: true);
        Session = Store.CompletePreparation(session, [version]);
        var records = new List<MergeChangeRecord>(); Store.ExportChangesSnapshot(Session, batch => records.AddRange(batch));
        Conflicts = records.ToDictionary(r => r.Conflict.SemanticSubject is { } subject ? subject.State.GetProperty("Ordinal").GetInt32() : 4, r => r.Conflict);
    }
    internal MergeDecisionChange Resolve(int ordinal, MergeResolutionChoice choice)
    {
        var conflict = Conflicts[ordinal]; var before = Session;
        Session = Store.ResolveBatch(before, [new(before.Plan.Revision, conflict.Id, conflict.InputSignature, choice)]);
        return new(before, Session, [conflict.Id]);
    }
    internal MergeDecisionChange Set(IReadOnlyDictionary<string, MergeResolution?> values)
    {
        var before = Session; Session = Store.SetResolutions(before, values);
        return new(before, Session, values.Keys.ToImmutableArray());
    }
    internal MergeDecisionChange Clear(int ordinal) => Set(new Dictionary<string, MergeResolution?> { [Conflicts[ordinal].Id] = null });
    internal SpatialMergeIndexBuild Build() => SpatialMergeIndexBuilder.Build(Store, Session, Path.Combine(Root, "indices"), Project);
    internal SpatialMergeIndexBuild BuildMetered(SpatialIndexDiagnostics diagnostics)
    {
        var directory = Path.Combine(Root, "metered-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var index = new SpatialWorkspaceIndex(Path.Combine(directory, "index.db"), diagnostics);
        var snapshot = Store.ExportChangesSnapshot(Session, batch => index.Put(batch.Select(Project), allowReplace: false));
        if (index.Counts().Total != snapshot.Total) throw new InvalidDataException("Incomplete metered fixture.");
        return new(index, snapshot, directory);
    }
    internal static SpatialWorkspaceRow Project(MergeChangeRecord record) => SpatialMergeRows.Create(record, t => t.Default, key => key);
    internal static string Rows(SpatialWorkspaceIndex index) => JsonSerializer.Serialize(index.Query("", "", 0, 100000).OrderBy(row => row.Id, StringComparer.Ordinal));
    private static MergeSubject Subject(int ordinal, string group)
    {
        var locked = ordinal == 2; var blocked = ordinal == 3;
        var automatic = locked || ordinal != 0 && ordinal != 3 && ordinal != 5 && ordinal % 100 != 99;
        return new("subject-" + ordinal, group, Hash("input-" + ordinal), Text("Chunk " + ordinal), Text("Choose the complete fixed chunk"),
            locked || blocked ? [] : [MergeDecisionKind.Ours, MergeDecisionKind.Theirs, MergeDecisionKind.Base, MergeDecisionKind.Delete],
            automatic, JsonSerializer.SerializeToElement(new { Ordinal = ordinal, AutomaticSide = ordinal % 100 >= 95 ? 2 : 1 }))
        {
            CanOverrideAutomatic = automatic && !locked, IsBlocked = blocked,
            SpatialLocations = [new("world", ordinal == 5 ? null : "layer", ordinal == 5 ? null : new(ordinal * 16d, 0, (ordinal + 1d) * 16, 16))]
        };
    }
    private static LocalizedText Text(string value) => new(value, new Dictionary<string, string>());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
