using System.IO.Compression;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class NativeArtifactStoreTests
{
    private static readonly PluginId Id = new("com.example.native");
    private static readonly ArtifactFormatRef Format = new(new(Id.Value), "full");
    private static readonly RestoreStrategyId Strategy = new(Id, "restore");
    private static readonly ConfigKindRef Kind = new(new(Id.Value), "world");

    [TestMethod]
    public async Task ClosureRoundtripPreservesIdentitiesAndDetectsStorageCorruption()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var ledger = await store.Ledger.LoadAsync();
        Assert.HasCount(2, ledger.Artifacts);
        var node = ledger.Artifacts.Single(n => n.ArtifactId == root);
        Assert.AreNotEqual(node.LogicalSha256, node.StorageSha256);
        var bundle = Path.Combine(fixture.Root, "closure.zip");
        await store.ExportAsync(root, bundle, default);
        var imported = new NativeArtifactStore(Path.Combine(fixture.Root, "imported"), new ZipCodec());
        await imported.ImportAsync(bundle, root, node.StorageSha256, "config", fixture.Folder.FolderId, default);
        await imported.ImportAsync(bundle, root, node.StorageSha256, "config", fixture.Folder.FolderId, default);
        Assert.HasCount(2, (await imported.Ledger.LoadAsync()).Artifacts);
        var reexported = Path.Combine(fixture.Root, "reexport.zip");
        await imported.ExportAsync(root, reexported, default);
        CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bundle)),
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(reexported)), "Replica retry bytes must not depend on local import transactions.");
        await using (var read = await imported.OpenAsync(root, default))
        {
            Assert.HasCount(2, read.Artifacts);
            var files = await read.Read.ListFilesAsync(read.Artifacts.Single(a => a.ArtifactId == root).Content, default);
            Assert.AreEqual("root.txt", files.Single().RelativePath);
        }
        await File.WriteAllTextAsync(imported.PayloadPath(node), "damaged");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => imported.OpenAsync(root, default));
        await imported.ImportAsync(bundle, root, node.StorageSha256, "config", fixture.Folder.FolderId, default);
        await using (await imported.OpenAsync(root, default)) { }
    }

    [TestMethod]
    public async Task GcWaitsForReadLeaseAndKeepsPendingClosure()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync(complete: false);
        Assert.IsEmpty(await store.CollectAsync([], default));
        var pending = store.Pending().Single(); store.CompletePending(pending.Id);
        var read = await store.OpenAsync(root, default);
        var collecting = store.CollectAsync([], default);
        Assert.IsFalse(collecting.IsCompleted);
        await read.DisposeAsync();
        Assert.HasCount(2, await collecting);
    }

    [TestMethod]
    public async Task SharedDependencyRemainsWhileEitherRootIsRetained()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync(diamond: true);
        var nodes = (await store.Ledger.LoadAsync()).Artifacts.ToDictionary(n => n.ArtifactId);
        var parents = nodes[root].Dependencies;
        var dependency = nodes[parents[0]].Dependencies.Single();
        Assert.AreEqual(dependency, nodes[parents[1]].Dependencies.Single());
        Assert.HasCount(1, await store.CollectAsync(parents, default));
        Assert.HasCount(1, await store.CollectAsync([parents[1]], default));
        CollectionAssert.AreEquivalent(new[] { parents[1], dependency }, (await store.Ledger.LoadAsync()).Artifacts.Select(n => n.ArtifactId).ToArray());
    }

    [TestMethod]
    public async Task CorruptPendingBlocksGcBeforeDeletingAnything()
    {
        using var fixture = new Fixture();
        var (store, _) = await fixture.CaptureAsync();
        await File.WriteAllTextAsync(Path.Combine(store.Root, "pending", "invalid.json"), "{");
        await Assert.ThrowsExactlyAsync<JsonException>(() => store.CollectAsync([], default));
        Assert.HasCount(2, (await store.Ledger.LoadAsync()).Artifacts);
    }

    [TestMethod]
    public async Task ImportRejectsForeignConfigAndIncompleteClosure()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var node = (await store.Ledger.LoadAsync()).Artifacts.Single(a => a.ArtifactId == root);
        var bundle = Path.Combine(fixture.Root, "closure.zip"); await store.ExportAsync(root, bundle, default);
        var imported = new NativeArtifactStore(Path.Combine(fixture.Root, "imported"), new ZipCodec());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => imported.ImportAsync(bundle, root, node.StorageSha256, "other", fixture.Folder.FolderId, default));
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update)) zip.Entries.First(e => e.Name == "payload.7z").Delete();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => imported.ImportAsync(bundle, root, node.StorageSha256, "config", fixture.Folder.FolderId, default));
        Assert.IsEmpty((await imported.Ledger.LoadAsync()).Artifacts);
    }

    [TestMethod]
    public void ConfigsSharingDestinationHaveSeparateNativeStores()
    {
        var destination = Path.GetFullPath("destination");
        Assert.AreNotEqual(NativeArtifactStore.ForConfig(destination, "one"), NativeArtifactStore.ForConfig(destination, "two"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "NativeArtifactTests", Guid.NewGuid().ToString("N"));
        public FolderSnapshot Folder { get; } = new(Guid.NewGuid(), "unused", "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        public async Task<(NativeArtifactStore Store, ArtifactId Root)> CaptureAsync(bool complete = true, bool diamond = false)
        {
            Directory.CreateDirectory(Root);
            var source = Path.Combine(Root, "primary"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "input"), "input bytes");
            var runtime = new PluginRuntimeManager();
            var manifest = new PluginManifestContract(Id, "1.0.0", PluginApiVersion.HostVersion, "unused.dll", "Unused",
                new("test", new Dictionary<string,string>()), new("test", new Dictionary<string,string>()), [], "unused.json",
                [HostServiceKind.ArtifactRead, HostServiceKind.ArtifactTransformStaging, HostServiceKind.RestoreMaterializationWorkspace],
                [PluginCapabilityKind.BackupArtifactTransformer, PluginCapabilityKind.RestoreMaterializer],
                [new(Format, 1, 1, new("full", new Dictionary<string,string>()))],
                [new(new(Id, "full"), [Kind], [CoreCaptureMode.Full], [ArtifactCompleteness.Complete],
                    JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(), [ArtifactTransformFailureBehavior.RequireTransform])],
                [new(Strategy, [new(Format, 1, 1)], [ArtifactCompleteness.Complete], [RestoreMode.Clean])], false);
            var config = new ConfigSnapshot("config", new("revision"), Kind, "world", [Folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var result = await runtime.ActivateAsync(new(Id, () => new GraphPlugin(diamond), new(Id, new Dictionary<string, JsonElement>()), [config], new Host(), new ActivationStore(), manifest));
            Assert.IsTrue(result.Success, JsonSerializer.Serialize(result.Diagnostics));
            var store = new NativeArtifactStore(Path.Combine(Root, "store"), new ZipCodec());
            await using var prepared = await store.PrepareAsync(runtime, config, Folder, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), source,
                new(new(Id, "full"), new Dictionary<string, JsonElement>(), ArtifactTransformFailureBehavior.RequireTransform), false, default);
            await prepared.SealAndCommitGraphAsync(default);
            if (complete) store.CompletePending(prepared.Intent.Id);
            return (store, prepared.Intent.Root);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class GraphPlugin(bool diamond) : IFolderRewindPlugin, IBackupArtifactTransformerCapability, IRestoreMaterializerCapability
    {
        public ArtifactTransformerId TransformerId => new(Id, "full");
        public RestoreStrategyId RestoreStrategyId => Strategy;
        public ValueTask<RestoreMaterializationResult> MaterializeAsync(RestoreMaterializationRequest request, PluginInvocationContext context)
            => throw new NotSupportedException("Storage tests do not claim domain materialization.");
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        { context.RegisterCapability<IPluginCapability>(this); return ValueTask.FromResult(PluginActivationResult.Empty); }
        public ValueTask DeactivateAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<ArtifactTransformResult> TransformAsync(ArtifactTransformRequest request, PluginInvocationContext context)
        {
            var dependency = await request.Staging.CreateArtifactAsync(default);
            var root = await request.Staging.CreateArtifactAsync(default);
            await using (var stream = await request.Staging.OpenWriteAsync(dependency.Staging, "dependency.txt", default))
                await stream.WriteAsync("shared bytes"u8.ToArray());
            await using (var stream = await request.Staging.OpenWriteAsync(root.Staging, "root.txt", default))
                await stream.WriteAsync("root bytes"u8.ToArray());
            if (diamond)
            {
                var left = await request.Staging.CreateArtifactAsync(default);
                var right = await request.Staging.CreateArtifactAsync(default);
                foreach (var item in new[] { left, right })
                {
                    await using var stream = await request.Staging.OpenWriteAsync(item.Staging, "branch.txt", default);
                    await stream.WriteAsync("branch bytes"u8.ToArray());
                }
                return new(OperationOutcome.Success, new(request.ExpectedGraphRevision,
                    [new(dependency.ArtifactId, dependency.Staging, Format, 1, Strategy, ArtifactCompleteness.Complete, CoreCaptureMode.Full, []),
                     new(left.ArtifactId, left.Staging, Format, 1, Strategy, ArtifactCompleteness.Complete, CoreCaptureMode.Full, [dependency.ArtifactId]),
                     new(right.ArtifactId, right.Staging, Format, 1, Strategy, ArtifactCompleteness.Complete, CoreCaptureMode.Full, [dependency.ArtifactId]),
                     new(root.ArtifactId, root.Staging, Format, 1, Strategy, ArtifactCompleteness.Complete, CoreCaptureMode.Full, [left.ArtifactId, right.ArtifactId])], root.ArtifactId), []);
            }
            return new(OperationOutcome.Success, new(request.ExpectedGraphRevision,
                [new(dependency.ArtifactId, dependency.Staging, Format, 1, Strategy, ArtifactCompleteness.Complete, CoreCaptureMode.Full, []),
                 new(root.ArtifactId, root.Staging, Format, 1, Strategy, ArtifactCompleteness.Complete, CoreCaptureMode.Full, [dependency.ArtifactId])], root.ArtifactId), []);
        }
    }
    private sealed class ActivationStore : IPluginActivationStore
    { public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class Host : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => throw new NotSupportedException();
        public IBackupRequestService Backups => throw new NotSupportedException();
        public IRestoreRequestService Restores => throw new NotSupportedException();
        public IHistoryQueryService History => throw new NotSupportedException();
        public IPluginNotificationService Notifications => throw new NotSupportedException();
        public IKnotLinkHostService KnotLink => throw new NotSupportedException();
        public IPluginDataStore DataStore => throw new NotSupportedException();
        public IPluginTemporaryStorage TemporaryStorage => throw new NotSupportedException();
        public IPluginLogger Logger => throw new NotSupportedException();
    }
    private sealed class ZipCodec : IArtifactEnvelopeCodec
    {
        public Task PackAsync(string source, string payload, bool encrypted, CancellationToken token)
        { ZipFile.CreateFromDirectory(source, payload); return Task.CompletedTask; }
        public Task UnpackAsync(string payload, string target, bool encrypted, CancellationToken token)
        { ZipFile.ExtractToDirectory(payload, target); return Task.CompletedTask; }
    }
}
