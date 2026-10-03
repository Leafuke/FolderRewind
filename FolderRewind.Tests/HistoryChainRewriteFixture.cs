using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

internal sealed class HistoryChainRewriteFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "chain-rewrite-" + Guid.NewGuid().ToString("N"));
    public HistoryRuntime History { get; private set; } = null!;
    public SevenZipArchiveProcessBackend Archive { get; }
    public RepresentationRuntime Engine { get; }
    public SourceId Source { get; } = SourceId.New();
    public HistoryConfigId Config { get; } = new(Guid.NewGuid().ToString("N"));
    public List<Node> Nodes { get; } = [];
    public HistoryChainRewritePlanner Planner => new(History, Engine);
    public HistoryChainRewriteExecutor Executor => new(History, Engine, Archive);
    public sealed record Node(SourceVersion Version, VersionRepresentation Representation, LocalReplicaCatalogEntry Entry,
        string StateDirectory, HistoryRewriteTree Tree);

    public string? Password { get; set; }
    public HistoryChainRewriteFixture(bool encrypted = false)
    {
        var exe = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        Assert.IsNotNull(exe, "Real 7-Zip is required; set FOLDERREWIND_TEST_7Z.");
        Password = encrypted ? "rewrite-test-password" : null;
        Archive = new(() => exe, () => Password, encrypted, ".restore-marker");
        Engine = new([new CoreArchiveRepresentationHandler(Archive), new SmartDeltaRepresentationHandler(Archive)]);
    }

    public async Task InitializeAsync()
    {
        History = new(new FileHistoryRepository(Config, new(Path.Combine(Root, "repository"))));
        await History.InitializeAsync();
        await History.WorkspaceStore.SaveAsync(new(Config, 0, []), -1);
        await History.LocalReplicaCatalogStore.SaveAsync(new(Config, 0, []), -1);
    }

    public async Task<Node> AddAsync(Node? parent, Action<string> change, bool full = false,
        CaptureScope scope = CaptureScope.FullSource, HistoryProvenance? provenance = null)
    {
        var directory = Path.Combine(Root, "states", Nodes.Count.ToString());
        Directory.CreateDirectory(directory);
        if (parent is not null)
        {
            foreach (var sub in Directory.EnumerateDirectories(parent.StateDirectory, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(directory, Path.GetRelativePath(parent.StateDirectory, sub)));
            foreach (var file in Directory.EnumerateFiles(parent.StateDirectory, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(directory, Path.GetRelativePath(parent.StateDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
        }
        change(directory);
        var tree = await HistoryRewriteTree.ReadAsync(directory);
        var version = new SourceVersion(VersionId.New(), Config, Source, parent is null ? [] : [parent.Version.VersionId],
            DateTimeOffset.UtcNow, null, scope, CaptureOutcome.Captured, [], new("source", directory), null, provenance ?? HistoryProvenance.Native("test"));
        var id = RepresentationId.New();
        full |= parent is null;
        var output = Path.Combine(Root, "archives", id.ToString());
        var payload = full ? await Archive.CreateFullAsync(version, directory, id, output, default)
            : await Archive.CreateDeltaAsync(version, directory, tree.ChangedFrom(parent!.Tree), id, output, default);
        var metadata = ImmutableDictionary<string, string>.Empty;
        if (!full) metadata = metadata.SetItem("deletedFiles", string.Join('\n', parent!.Tree.Files.Keys.Except(tree.Files.Keys)));
        var representation = new VersionRepresentation(id, version.VersionId,
            full ? RepresentationKind.CoreFull : RepresentationKind.CoreSmartDelta, "7z", full ? [] : [parent!.Representation.RepresentationId],
            MaterializationFidelity.Exact, null, null, metadata);
        var entry = new LocalReplicaCatalogEntry(id, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload.PayloadPath), DateTimeOffset.UtcNow);
        var codec = new HistoryPackCodec();
        await History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { version, representation }.Select(o => codec.CreateObject(o))));
        var catalog = (await History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        await History.LocalReplicaCatalogStore.SaveAsync(new(Config, catalog.CatalogRevision + 1, catalog.Entries.Append(entry)), catalog.CatalogRevision);
        await History.EnsureIndexCurrentAsync();
        var node = new Node(version, representation, entry, directory, tree);
        Nodes.Add(node);
        return node;
    }

    public HistoryChainRewriteRequest Delete(params Node[] nodes) => new(HistoryChainRewriteOrigin.Manual,
        [.. nodes.Select(n => n.Entry.LocalReplicaId)], [.. nodes.Select(n => n.Version.VersionId)], [], 5, true, true);

    public async Task RecordBackupAsync(Node node)
    {
        var runId = RunId.New();
        var checkpoint = new SourceCheckpoint(CheckpointId.New(), Config, node.Version.CreatedAtUtc, runId,
            HistoryProvenance.Native("test"), [new CheckpointSource(Source, node.Version.SourceDescriptorSnapshot,
                node.Version.VersionId, CheckpointSourceDisposition.Captured)]);
        var run = new BackupRun(runId, Config, node.Version.CreatedAtUtc, node.Version.CreatedAtUtc,
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(Source, BackupRunSourceOutcome.Captured, node.Version.VersionId, [], checkpoint.CheckpointId)], []);
        var codec = new HistoryPackCodec();
        await History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { checkpoint, run }.Select(o => codec.CreateObject(o))));
        await History.EnsureIndexCurrentAsync();
    }

    public HistoryChainRewriteRetentionService Retention()
    {
        async Task<IRepresentationEnvironment> Environment(CancellationToken token)
            => new RepresentationEnvironment((await History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []);
        return new(History, Engine, Archive, new(History, Engine, Environment, new FileSystemHistoryLocalPayloadStore()));
    }

    public async Task AssertRestoresAsync(params Node[] nodes)
    {
        var graph = await History.Query.GetAllRepresentationsAsync();
        var environment = new RepresentationEnvironment((await History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        foreach (var node in nodes)
        {
            var assessment = await Engine.AssessVersionAsync(node.Version.VersionId, graph, environment, AssessmentDepth.Deep, MaterializationFidelity.Exact);
            Assert.AreEqual(HistoryReadiness.Ready, assessment.Readiness);
            var target = Path.Combine(Root, "checks", Guid.NewGuid().ToString("N"));
            await Engine.MaterializeAsync(assessment.Selected!.RepresentationId, graph, environment, MaterializationFidelity.Exact, target);
            Assert.IsTrue(node.Tree.EquivalentTo(await HistoryRewriteTree.ReadAsync(target)), "Restored content differs for " + node.Version.VersionId);
        }
    }

    public async Task RestartAsync()
    {
        await History.DisposeAsync();
        History = new(new FileHistoryRepository(Config, new(Path.Combine(Root, "repository"))));
        await History.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (History is not null) await History.DisposeAsync();
        if (!Directory.Exists(Root)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}
