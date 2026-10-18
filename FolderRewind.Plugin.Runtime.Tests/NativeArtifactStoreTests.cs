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
    public async Task TinyMetadataClosureAcceptsTheNodeLimitAndRejectsTheNextNodeWithoutPayloadAllocation()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var ledger = await store.Ledger.LoadAsync();
        var template = ledger.Artifacts.Single(n => n.ArtifactId != root);
        ArtifactLedgerEntry Leaf()
        {
            var id = new ArtifactId(Guid.NewGuid());
            return template with { ArtifactId = id, ContentRelativePath = "objects/" + id + "/payload", LogicalSize = 1, StorageSize = 1, LogicalEntryCount = 1 };
        }
        var leaves = Enumerable.Range(0, ArtifactLedgerValidator.MaximumArtifacts - 1).Select(_ => Leaf()).ToArray();
        var checkpoint = ledger.Artifacts.Single(n => n.ArtifactId == root) with { Dependencies = leaves.Select(n => n.ArtifactId).ToArray() };
        var bounded = ledger with { Artifacts = leaves.Append(checkpoint).ToArray() };
        Assert.HasCount(4096, NativeArtifactClosureLimits.Validate(bounded, root).Reachable);
        var next = Leaf();
        var oversized = bounded with { Artifacts = bounded.Artifacts.Append(next).ToArray() };
        Assert.ThrowsExactly<InvalidDataException>(() => NativeArtifactClosureLimits.Validate(oversized, root));
        Assert.HasCount(2, (await store.Ledger.LoadAsync()).Artifacts);
        await using var readable = await store.OpenAsync(root, default);
        Assert.HasCount(2, readable.Artifacts);
    }

    [TestMethod]
    public async Task PreparedGraphDoesNotReleaseTheBorrowedOperationBeforeConsistencyAndStreamsFinish()
    {
        using var fixture = new Fixture();
        _ = await fixture.CaptureAsync(borrowedOperation: true);
    }

    [TestMethod]
    public async Task ParallelEnvelopeJobsStayBoundedAndCorruptionDoesNotReturnAReadLease()
    {
        using var fixture = new Fixture();
        var codec = new ZipCodec(2);
        var (store, root) = await fixture.CaptureAsync(diamond: true, suppliedCodec: codec);
        Assert.AreEqual(2, codec.PeakWorkers);
        await using (var read = await store.OpenAsync(root, default)) Assert.HasCount(4, read.Artifacts);
        codec.FailUnpack = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenAsync(root, default));
        Assert.AreEqual(0, codec.Workers);
        Assert.IsFalse(Directory.Exists(Path.Combine(store.Root, ".read"))
            && Directory.EnumerateDirectories(Path.Combine(store.Root, ".read")).Any());
    }

    [TestMethod]
    public async Task ClosureLimitsAreSymmetricAndMetadataIsBoundedBeforeDeserialization()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var graph = await store.Ledger.LoadAsync();
        foreach (var logical in new[] { false, true })
        {
            var oversized = graph with { Artifacts = graph.Artifacts.Select(n => logical
                ? n with { LogicalSize = ArtifactTransformLimits.Default.MaximumBytes / 2 + 1 }
                : n with { StorageSize = ArtifactTransformLimits.Default.MaximumBytes / 2 + 1 }).ToArray() };
            Assert.ThrowsExactly<InvalidDataException>(() => NativeArtifactClosureLimits.Validate(oversized, root));
        }
        var descriptor = Path.Combine(fixture.Root, "oversized.json");
        await File.WriteAllBytesAsync(descriptor, new byte[4097]);
        Assert.ThrowsExactly<InvalidDataException>(() => ArtifactMetadataFile.ReadJson<NativeEnvelope>(descriptor, 4096));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ArtifactMetadataFile.ReadStreamAsync(new MemoryStream(new byte[4097]), 4096, default));
    }

    [TestMethod]
    public async Task PreparedCandidateReusesFreshlyVerifiedOutputsWithoutUnpackingAgain()
    {
        using var fixture = new Fixture();
        await fixture.CaptureAsync(validatePrepared: true);
    }

    [TestMethod]
    public async Task ObjectClosureUploadsOnlyMissingBytesAndRejectsCorruptDownload()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var node = (await store.Ledger.LoadAsync()).Artifacts.Single(n => n.ArtifactId == root);
        var transport = new ObjectTransport(); var descriptor = Path.Combine(fixture.Root, "objects.json");
        await store.ExportObjectsAsync(root, descriptor, transport, default);
        var firstDescriptor = File.ReadAllBytes(descriptor);
        Assert.AreEqual(2, transport.Uploads);
        await store.ExportObjectsAsync(root, descriptor, transport, default);
        Assert.AreEqual(2, transport.Uploads);
        CollectionAssert.AreEqual(firstDescriptor, File.ReadAllBytes(descriptor));
        var imported = new NativeArtifactStore(Path.Combine(fixture.Root, "objects-import"), new ZipCodec());
        await imported.ImportObjectsAsync(descriptor, root, node.StorageSha256, "config", fixture.Folder.FolderId, transport, default);
        await using (var read = await imported.OpenAsync(root, default)) Assert.HasCount(2, read.Artifacts);
        var key = transport.Bytes.Keys.First(); transport.Bytes[key][0] ^= 0x40;
        var rejected = new NativeArtifactStore(Path.Combine(fixture.Root, "rejected"), new ZipCodec());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => rejected.ImportObjectsAsync(descriptor, root,
            node.StorageSha256, "config", fixture.Folder.FolderId, transport, default));
        Assert.IsEmpty((await rejected.Ledger.LoadAsync()).Artifacts);
        // A corrupt object is no longer considered present; retry repairs just that object.
        await store.ExportObjectsAsync(root, descriptor, transport, default);
        Assert.AreEqual(3, transport.Uploads);
    }

    [TestMethod]
    public async Task CanceledObjectUploadCannotPublishDescriptor()
    {
        using var fixture = new Fixture(); var (store, root) = await fixture.CaptureAsync();
        var descriptor = Path.Combine(fixture.Root, "objects.json"); var transport = new ObjectTransport { CancelUpload = true };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.ExportObjectsAsync(root, descriptor, transport, default));
        Assert.IsFalse(File.Exists(descriptor));
    }

    private sealed class ObjectTransport : INativeArtifactObjectTransport
    {
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public int Uploads { get; private set; }
        public bool CancelUpload { get; init; }
        public Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token)
            => Task.FromResult(Bytes.TryGetValue(key, out var bytes) && bytes.LongLength == size
                && StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), sha256));
        public Task UploadObjectAsync(string key, string path, long size, string sha256, ArtifactTransferBudget budget, CancellationToken token)
        { if (CancelUpload) throw new OperationCanceledException(); Bytes[key] = File.ReadAllBytes(path); Uploads++; return Task.CompletedTask; }
        public async Task DownloadObjectAsync(string key, string path, long expectedLength, ArtifactTransferBudget budget, CancellationToken token)
        {
            await using var target = File.Create(path);
            await budget.CopyAsync(new MemoryStream(Bytes[key], false), target, expectedLength, token);
        }
    }

    [TestMethod]
    public async Task LazyLeaseDoesNotReadUntouchedDependencyButStillRejectsItWhenOpened()
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var ledger = await store.Ledger.LoadAsync();
        var dependency = ledger.Artifacts.Single(n => n.ArtifactId != root);
        var path = store.PayloadPath(dependency);
        var bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 0x40; File.WriteAllBytes(path, bytes);
        await using (var read = await store.OpenLazyAsync(root, default))
        {
            var rootHandle = read.Artifacts.Single(a => a.ArtifactId == root).Content;
            Assert.AreEqual("root.txt", (await read.Read.ListFilesAsync(rootHandle, default)).Single().RelativePath);
            var badHandle = read.Artifacts.Single(a => a.ArtifactId != root).Content;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await read.Read.ListFilesAsync(badHandle, default));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenAsync(root, default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenLazyAsync(root, default));
    }

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
    [DataRow("RepairPrepared:after")]
    [DataRow("RepairOldMoved:after")]
    [DataRow("RepairInstalled:after")]
    public async Task ImportRepairsSurviveDirectorySwapInterruptionAndRepeatedRecovery(string fault)
    {
        using var fixture = new Fixture();
        var (store, root) = await fixture.CaptureAsync();
        var node = (await store.Ledger.LoadAsync()).Artifacts.Single(n => n.ArtifactId == root);
        var bundle = Path.Combine(fixture.Root, "repair.zip"); await store.ExportAsync(root, bundle, default);
        await File.WriteAllTextAsync(store.PayloadPath(node), "damaged");
        var failed = new NativeArtifactStore(store.Root, new ZipCodec(), stage =>
        { if (stage == fault) throw new IOException("injected repair interruption"); });
        await Assert.ThrowsExactlyAsync<IOException>(() => failed.ImportAsync(bundle, root, node.StorageSha256,
            "config", fixture.Folder.FolderId, default));
        await store.RecoverAsync((_, _) => Task.FromResult(false), default);
        await store.RecoverAsync((_, _) => Task.FromResult(false), default);
        await using (await store.OpenAsync(root, default)) { }
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(store.Root, "repairs")).Any());
        Assert.HasCount(2, (await store.Ledger.LoadAsync()).Artifacts);
    }

    [TestMethod]
    [DataRow("GcPrepared:after")]
    [DataRow("GcGraphCommitted:after")]
    [DataRow("GcPhysicalDelete:after")]
    public async Task GcRecoversPhysicalDeletionAndAccountingAcrossPublicationBoundaries(string fault)
    {
        using var fixture = new Fixture();
        var (store, _) = await fixture.CaptureAsync();
        var nodes = (await store.Ledger.LoadAsync()).Artifacts;
        var physicalBytes = nodes.Sum(n => Directory.EnumerateFiles(Path.GetDirectoryName(store.PayloadPath(n))!).Sum(p => new FileInfo(p).Length));
        var failed = new NativeArtifactStore(store.Root, new ZipCodec(), stage => { if (stage == fault) throw new IOException("injected GC interruption"); });
        await Assert.ThrowsExactlyAsync<IOException>(() => failed.CollectAsync([], default));
        await store.RecoverAsync((_, _) => Task.FromResult(false), default);
        await store.RecoverAsync((_, _) => Task.FromResult(false), default);
        var receipts = await store.CollectDetailedAsync([], default);
        Assert.AreEqual(physicalBytes, receipts.SelectMany(r => r.Objects).Sum(o => o.PhysicalBytes));
        Assert.HasCount(2, receipts.SelectMany(r => r.Objects).ToArray());
        Assert.IsTrue(nodes.All(n => !File.Exists(store.PayloadPath(n))));
        await store.AcknowledgeCollectionsAsync(receipts.Select(r => r.Id), default);
        Assert.IsEmpty(await store.CollectAsync([], default));
    }

    [TestMethod]
    public async Task ConfigurationScopeEndsAbandonedInMemoryOwnershipWithoutRemovingPersistentEvidence()
    {
        using var fixture = new Fixture();
        using var operation = NativeArtifactStore.TrackConfigurationCapture("config");
        var (store, _) = await fixture.CaptureAsync(complete: false, transferPendingOwnership: true);
        await store.RecoverAsync((_, _) => Task.FromResult(false), default);
        Assert.HasCount(1, store.Pending());
        operation.Dispose();
        Assert.HasCount(1, store.Pending());
        await store.RecoverAsync((_, _) => Task.FromResult(false), default);
        Assert.IsEmpty(store.Pending());
        Assert.HasCount(2, await store.CollectAsync([], default));
    }

    [TestMethod]
    public async Task CorruptGcIntentAndMissingProtectedRootKeepPhysicalObjects()
    {
        using var fixture = new Fixture();
        var (store, _) = await fixture.CaptureAsync();
        var nodes = (await store.Ledger.LoadAsync()).Artifacts;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.CollectAsync([new(Guid.NewGuid())], default));
        var failed = new NativeArtifactStore(store.Root, new ZipCodec(), stage => { if (stage == "GcGraphCommitted:after") throw new IOException("injected GC interruption"); });
        await Assert.ThrowsExactlyAsync<IOException>(() => failed.CollectAsync([], default));
        var journal = Directory.EnumerateFiles(Path.Combine(store.Root, "gc"), "*.json").Single();
        await File.WriteAllTextAsync(journal, "{}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.RecoverAsync((_, _) => Task.FromResult(false), default));
        Assert.IsTrue(nodes.All(n => File.Exists(store.PayloadPath(n))));
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
    public async Task CollectingOneConfigDoesNotRemoveAnotherConfigAtTheSameDestination()
    {
        using var first = new Fixture(); using var second = new Fixture();
        var destination = Path.Combine(first.Root, "shared-destination");
        var one = await first.CaptureAsync(storageRoot: NativeArtifactStore.ForConfig(destination, "one"), configId: "one");
        var two = await second.CaptureAsync(storageRoot: NativeArtifactStore.ForConfig(destination, "two"), configId: "two");
        Assert.HasCount(2, await one.Store.CollectAsync([], default));
        await using var read = await two.Store.OpenAsync(two.Root, default);
        Assert.HasCount(2, read.Artifacts);
        Assert.IsTrue(read.Artifacts.All(a => a.ConfigId == "two"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "NativeArtifactTests", Guid.NewGuid().ToString("N"));
        public FolderSnapshot Folder { get; } = new(Guid.NewGuid(), "unused", "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        public async Task<(NativeArtifactStore Store, ArtifactId Root)> CaptureAsync(bool complete = true, bool diamond = false,
            string? storageRoot = null, string configId = "config", bool validatePrepared = false, ZipCodec? suppliedCodec = null, bool transferPendingOwnership = false, bool borrowedOperation = false)
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
            var config = new ConfigSnapshot(configId, new("revision"), Kind, "world", [Folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var result = await runtime.ActivateAsync(new(Id, () => new GraphPlugin(diamond), new(Id, new Dictionary<string, JsonElement>()), [config], new Host(), new ActivationStore(), manifest));
            Assert.IsTrue(result.Success, JsonSerializer.Serialize(result.Diagnostics));
            var codec = suppliedCodec ?? new ZipCodec();
            var store = new NativeArtifactStore(storageRoot ?? Path.Combine(Root, "store"), codec);
            using var operation = borrowedOperation ? runtime.TryAcquireOperation(Id) : null;
            var prepared = await store.PrepareAsync(runtime, config, Folder, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), source,
                new(new(Id, "full"), new Dictionary<string, JsonElement>(), ArtifactTransformFailureBehavior.RequireTransform), false, default, operation: operation);
            var capturedRoot = prepared.Intent.Root;
            await using (prepared)
            {
            await prepared.SealAndCommitGraphAsync(default);
            if (transferPendingOwnership) prepared.TransferPendingOwnership();
            if (validatePrepared)
            {
                var unpacked = codec.Unpacks;
                await using var candidate = prepared.OpenLazy();
                foreach (var node in candidate.Artifacts)
                {
                    var file = (await candidate.Read.ListFilesAsync(node.Content, default)).Single();
                    await using var input = await candidate.Read.OpenReadAsync(node.Content, file.RelativePath, default);
                    using var bytes = new MemoryStream(); await input.CopyToAsync(bytes);
                    Assert.IsGreaterThan(0, bytes.Length);
                }
                Assert.AreEqual(unpacked, codec.Unpacks, "Already verified candidate bytes must not be decompressed a second time.");
            }
            if (complete) store.CompletePending(prepared.Intent.Id);
            }
            if (operation is not null)
            {
                Assert.IsNotNull(operation.Find<IRestoreMaterializerCapability>(m => m.RestoreStrategyId == Strategy));
                var drain = runtime.DeactivateAsync(Id);
                Assert.IsFalse(drain.IsCompleted);
                operation.Dispose();
                await drain;
            }
            return (store, capturedRoot);
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
    private sealed class ZipCodec(int concurrency = 1) : IConcurrentArtifactEnvelopeCodec, IBoundedArtifactEnvelopeCodec
    {
        private int _unpacks;
        public int Unpacks => _unpacks;
        public int MaximumConcurrency => concurrency;
        public int Workers, PeakWorkers;
        public bool FailUnpack;
        private async Task Work(Action action, CancellationToken token)
        {
            var count = Interlocked.Increment(ref Workers);
            int old;
            do { old = PeakWorkers; } while (old < count && Interlocked.CompareExchange(ref PeakWorkers, count, old) != old);
            try { if (concurrency > 1) await Task.Delay(20, token); action(); }
            finally { Interlocked.Decrement(ref Workers); }
        }
        public Task PackAsync(string source, string payload, bool encrypted, CancellationToken token)
            => Work(() => ZipFile.CreateFromDirectory(source, payload), token);
        public Task UnpackAsync(string payload, string target, bool encrypted, long maximumBytes, ArtifactExtractionBudget budget, CancellationToken token)
        {
            using var zip = ZipFile.OpenRead(payload);
            var bytes = zip.Entries.Sum(e => e.Length);
            if (bytes > maximumBytes) throw new InvalidDataException("Declared node byte limit exceeded.");
            budget.Reserve(bytes, zip.Entries.Count);
            return UnpackAsync(payload, target, encrypted, token);
        }
        public Task UnpackAsync(string payload, string target, bool encrypted, CancellationToken token)
            => Work(() => { if (FailUnpack) throw new InvalidDataException("Injected decoder failure.");
                Interlocked.Increment(ref _unpacks); ZipFile.ExtractToDirectory(payload, target); }, token);
    }
}
