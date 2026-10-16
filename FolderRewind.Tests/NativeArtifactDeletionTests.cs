using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeArtifactDeletionTests
{
    [TestMethod]
    public async Task DeletingVersionKeepsSharedClosureUntilLastOwnerIsDeleted()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var a = await f.AddAsync(); var b = await f.AddAsync(a);
        var service = new NativeArtifactVersionDeletion(f.History, f.Store);
        var prepared = await f.PrepareAsync(service, a);
        var result = await service.CommitAsync(prepared, default);
        Assert.IsTrue(result.Committed); Assert.IsFalse(result.CleanupPending);
        Assert.AreEqual(0L, result.ReclaimedBytes);
        Assert.IsTrue(File.Exists(f.Store.PayloadPath(a.Node)));
        Assert.HasCount(1, (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
        Assert.IsTrue((await f.History.Query.GetAnnotationProjectionAsync(new(HistoryAnnotationTargetKind.Version, a.Version.VersionId.Value))).IsSuppressed);
        var last = await service.CommitAsync(await f.PrepareAsync(service, b), default);
        Assert.AreEqual(2L, last.ReclaimedBytes);
        Assert.IsEmpty((await f.Store.Ledger.LoadAsync()).Artifacts);
        Assert.IsFalse(File.Exists(f.Store.PayloadPath(a.Node)));
    }

    [TestMethod]
    public async Task DurableDeletionRecoversCatalogThenCollectsWithoutRetentionEnabled()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var a = await f.AddAsync();
        var service = new NativeArtifactVersionDeletion(f.History, f.Store, stage =>
        { if (stage == "pack-installed") throw new IOException("injected crash boundary"); });
        var result = await service.CommitAsync(await f.PrepareAsync(service, a), default);
        Assert.IsTrue(result.Committed); Assert.IsTrue(result.RecoveryRequired);
        Assert.IsTrue(File.Exists(f.Store.PayloadPath(a.Node)));
        await f.RestartAsync();
        Assert.IsEmpty((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
        var reclaimed = await NativeArtifactRetention.CollectAsync(f.History, f.Planner(), f.Store, 0, default);
        Assert.AreEqual(1L, reclaimed.Sum(r => r.ReclaimedBytes));
        Assert.IsEmpty(await NativeArtifactRetention.CollectAsync(f.History, f.Planner(), f.Store, 0, default));
    }

    [TestMethod]
    public async Task WorkspaceProtectionAndChangedPreparationCannotBeBypassed()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var a = await f.AddAsync();
        await f.History.WorkspaceStore.SaveAsync(new(f.History.ConfigId, 1,
            [new WorkspaceSourceBaseline(a.Version.SourceId, a.Version.VersionId, WorkspaceBaselineRelation.Exact)]), 0);
        var service = new NativeArtifactVersionDeletion(f.History, f.Store);
        await Assert.ThrowsExactlyAsync<MaterializationPolicyCommandException>(() => f.PrepareAsync(service, a));
        await f.History.WorkspaceStore.SaveAsync(new(f.History.ConfigId, 2, []), 1);
        var prepared = await f.PrepareAsync(service, a);
        await f.History.Annotations.SetCommentAsync(new(HistoryAnnotationTargetKind.Version, a.Version.VersionId.Value), "changed");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.CommitAsync(prepared, default));
        Assert.IsTrue(File.Exists(f.Store.PayloadPath(a.Node)));
        Assert.HasCount(1, (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }

    [TestMethod]
    public async Task SameProcessRecoveryScansAgainAfterASuccessfulEarlierPass()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var recovery = new HistoryRestoreService(f.History,
            new([new CoreArchiveRepresentationHandler(new UnusedCodec())]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend())
        { RecoverNativeInsideOperation = (_, token) => NativeArtifactHistoryRecovery.RecoverAsync(f.History, f.Store, token) };
        for (var i = 0; i < 2; i++)
        {
            await f.Store.SavePendingAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), new(Guid.NewGuid()), "Prepared"), default);
            Assert.HasCount(1, f.Store.Pending());
            await recovery.RecoverIncompleteAsync();
            Assert.IsEmpty(f.Store.Pending());
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "NativeDeletion", Guid.NewGuid().ToString("N"));
        private readonly HistoryConfigId config = new(Guid.NewGuid().ToString("N"));
        private readonly SourceId source = SourceId.New();
        public HistoryRuntime History { get; private set; } = null!;
        public NativeArtifactStore Store { get; private set; } = null!;
        public sealed record Item(SourceVersion Version, VersionRepresentation Representation, ArtifactLedgerEntry Node);
        public async Task InitializeAsync()
        {
            Store = new(Path.Combine(root, "store"), new UnusedCodec());
            History = new(new FileHistoryRepository(config, new(Path.Combine(root, "history"))));
            await History.InitializeAsync();
            await History.WorkspaceStore.SaveAsync(new(config, 0, []), -1);
            await History.LocalReplicaCatalogStore.SaveAsync(new(config, 0, []), -1);
        }
        public async Task<Item> AddAsync(Item? dependency = null)
        {
            var id = new ArtifactId(Guid.NewGuid());
            var version = new SourceVersion(VersionId.New(), config, source, [], DateTimeOffset.UtcNow, null,
                CaptureScope.FullSource, CaptureOutcome.Captured, [], new("test", "isolated"), null, HistoryProvenance.Native("test"));
            var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.PluginArtifact,
                "test-format", [], MaterializationFidelity.Exact, null, null, new Dictionary<string, string>
                { ["nativeArtifactContractVersion"] = "2", ["incrementalEvidence"] = "2", ["artifactRootId"] = id.Value.ToString("D") });
            var node = new ArtifactLedgerEntry(id, new(new("plugin.test"), "test-format"), 1, new(new("plugin.test"), "restore"),
                config.Value, source.Value, "artifacts/" + id.Value.ToString("N"), new string('A', 64), 1, new string('B', 64), 1,
                ArtifactCompleteness.Complete, CoreCaptureMode.Full, dependency is null ? [] : [dependency.Node.ArtifactId],
                Guid.NewGuid().ToString("N"), ArtifactAvailability.Available, ArtifactAvailability.Missing);
            var ledger = await Store.Ledger.LoadAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(Store.PayloadPath(node))!);
            await File.WriteAllBytesAsync(Store.PayloadPath(node), [1]);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            await File.WriteAllTextAsync(Path.Combine(Store.Root, ".folderrewind", "artifact-ledger.v2.json"),
                JsonSerializer.Serialize(ledger with { Revision = new(Guid.NewGuid().ToString("N")), Artifacts = ledger.Artifacts.Append(node).ToArray() }, options));
            var codec = new HistoryPackCodec();
            await History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
                new object[] { version, representation }.Select(o => codec.CreateObject(o))));
            await History.EnsureIndexCurrentAsync();
            var catalog = (await History.LocalReplicaCatalogStore.LoadAsync()).Value!;
            await History.LocalReplicaCatalogStore.SaveAsync(new(config, catalog.CatalogRevision + 1, catalog.Entries.Append(
                new(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(Store.PayloadPath(node)), DateTimeOffset.UtcNow))), catalog.CatalogRevision);
            return new(version, representation, node);
        }
        public Task<PreparedNativeArtifactDeletion> PrepareAsync(NativeArtifactVersionDeletion service, Item item)
            => service.PrepareAsync(item.Version.VersionId, item.Representation.RepresentationId, Store.PayloadPath(item.Node), root, true, true, default);
        public HistoryRetentionPlanner Planner() => new(History, new([new CoreArchiveRepresentationHandler(new UnusedCodec())]), _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])), new FileSystemHistoryLocalPayloadStore());
        public async Task RestartAsync()
        {
            await History.DisposeAsync(); History = new(new FileHistoryRepository(config, new(Path.Combine(root, "history"))));
            await History.InitializeAsync();
        }
        public async ValueTask DisposeAsync()
        { if (History is not null) await History.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class UnusedCodec : IArtifactEnvelopeCodec, IArchiveRepresentationBackend
    {
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
            => throw new AssertFailedException("Orphan collection must not verify Core archives.");
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string target, CancellationToken token)
            => throw new AssertFailedException("Orphan collection must not materialize Core archives.");
        public Task PackAsync(string source, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Deletion must not rebuild envelopes.");
        public Task UnpackAsync(string source, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Deletion must not decode envelopes.");
    }
}
