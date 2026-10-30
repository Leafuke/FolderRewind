using System.IO.Compression;
using System.Text.Json.Nodes;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeMigrationPayloadTests
{
    private static async Task<string> ExportAsync(NativeCloudPayloadTests.Fixture f, bool embedded = true, DeepProbe? probe = null)
    {
        var path = Path.Combine(f.Root, "migration.frhistory");
        var archive = new CoreArchive();
        var project = new HistoryMigrationExportProject(new(f.History.ConfigId, "native", [new(f.Source, "source", "isolated")], f.History),
            Path.Combine(f.Root, "backups"), archive)
        {
            NativeStore = f.Store,
            Consumers = new([new CoreArchiveRepresentationHandler(archive), new PluginArtifactRepresentationHandler(probe ?? new DeepProbe())])
        };
        await new HistoryMigrationBundleService().ExportAsync([project], path, "test", null, embedded);
        return path;
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MixedCoreAndNativePackageKeepsIndependentPayloadContracts(bool supported)
    {
        await using var f = new NativeCloudPayloadTests.Fixture(); await f.InitializeAsync(supported);
        var root = Path.Combine(f.Root, "backups"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "formal.7z"); await File.WriteAllTextAsync(path, "formal archive fixture");
        var core = new VersionRepresentation(RepresentationId.New(), f.Representation.VersionId, RepresentationKind.CoreFull,
            "7z", [], MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["fileName"] = "formal.7z" });
        await f.History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [new FolderRewind.History.Storage.HistoryPackCodec().CreateObject(core)])); await f.History.EnsureIndexCurrentAsync();
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        await f.History.LocalReplicaCatalogStore.SaveAsync(new(f.History.ConfigId, catalog.CatalogRevision + 1,
            [new(core.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow)]), catalog.CatalogRevision);
        var preview = await new HistoryMigrationBundleService().PreviewAsync(await ExportAsync(f));
        var migration = preview.History.Projects.Single().Migration!;
        Assert.HasCount(1, migration.Replicas); Assert.HasCount(1, migration.PayloadFiles);
        Assert.HasCount(supported ? 1 : 0, migration.NativePayloads); Assert.HasCount(supported ? 2 : 0, migration.NativeObjects);
        Assert.AreEqual(supported ? 4 : 3, preview.History.FormatVersion);
        Assert.AreEqual("formal.7z", migration.PayloadFiles.Single().RelativePath);
    }

    [TestMethod]
    public async Task NativePackageUsesVirtualPathsAndSurvivesInterruptedInstallationAtAnotherRoot()
    {
        await using var f = new NativeCloudPayloadTests.Fixture(); await f.InitializeAsync();
        var probe = new DeepProbe(); var bundle = await ExportAsync(f, probe: probe);
        Assert.AreEqual(1, probe.DeepCalls, "Export must require reconstructed source validation from its explicit consumer.");
        var service = new HistoryMigrationBundleService(); var preview = await service.PreviewAsync(bundle);
        Assert.AreEqual(4, preview.History.FormatVersion);
        var migration = preview.History.Projects.Single().Migration!;
        Assert.HasCount(1, migration.NativePayloads); Assert.HasCount(2, migration.NativeObjects);
        Assert.IsEmpty(migration.PayloadFiles); Assert.IsNull(migration.ArtifactClosure);
        using (var zip = ZipFile.OpenRead(bundle)) Assert.IsTrue(zip.Entries.Where(e => e.FullName.StartsWith("native/", StringComparison.Ordinal))
            .All(e => e.FullName.StartsWith("native/v2/", StringComparison.Ordinal) && !e.FullName.Contains(f.Store.Root, StringComparison.OrdinalIgnoreCase)));
        var root = Path.Combine(f.Root, "relocated-backups"); var config = Path.Combine(f.Root, "fresh-config");
        service.PayloadCheckpoint = stage => { if (stage == "payload-installed") throw new IOException("injected Native installation interruption"); };
        await Assert.ThrowsExactlyAsync<IOException>(() => service.InstallPayloadsAsync(bundle, preview, f.History.ConfigId, config, root));
        service.PayloadCheckpoint = null; await service.RecoverPayloadsAsync(config, f.History.ConfigId, root);
        await service.InstallPayloadsAsync(bundle, preview, f.History.ConfigId, config, root);
        var payload = migration.NativePayloads.Single();
        var imported = new NativeArtifactStore(Path.Combine(f.Root, "restored-store"), new NativeCloudPayloadTests.ZipCodec());
        await imported.ImportObjectsAsync(HistoryMigrationBundleService.InstalledNativePath(root, payload.EntryName), new(payload.ArtifactRootId),
            payload.RootStorageSha256, payload.ArtifactConfigId, payload.SourceId, HistoryMigrationBundleService.InstalledNativeObjects(migration, root), default);
        await using (var read = await imported.OpenAsync(f.ArtifactRoot, default)) Assert.HasCount(2, read.Nodes);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "native")), "Package virtual names must never become user backup paths.");
    }

    [TestMethod]
    public async Task MaterializationEvidenceIncludesSharedDependencyBytesAndEnvelopePolicy()
    {
        await using var f = new NativeCloudPayloadTests.Fixture(); await f.InitializeAsync();
        var ledger = await f.Store.Ledger.LoadAsync(); var root = ledger.Artifacts.Single(n => n.ArtifactId == f.ArtifactRoot);
        var entries = new[] { new LocalReplicaCatalogEntry(f.Representation.RepresentationId, LocalReplicaId.New(),
            LocalReplicaLocator.ControlledAbsolute(f.Store.PayloadPath(root)), DateTimeOffset.UtcNow) };
        var versions = await f.History.Query.GetAllVersionsAsync();
        Task<string> Capture() => NativeArtifactMaterializationEvidence.CaptureAsync(f.History.ConfigId, f.Store,
            [f.Representation], versions, entries, default);
        var before = await Capture();
        var metadata = Path.Combine(Path.GetDirectoryName(f.Store.PayloadPath(root))!, "envelope.json");
        var original = await File.ReadAllBytesAsync(metadata);
        await File.WriteAllBytesAsync(metadata, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new NativeEnvelope(1, true)));
        Assert.AreNotEqual(before, await Capture());
        await File.WriteAllBytesAsync(metadata, original);
        var dependency = ledger.Artifacts.Single(n => n.ArtifactId != f.ArtifactRoot);
        var path = f.Store.PayloadPath(dependency); var bytes = File.ReadAllBytes(path); bytes[0] ^= 0x40; File.WriteAllBytes(path, bytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(Capture);
    }

    [TestMethod]
    public async Task DescriptorSourceBindingAndDamagedObjectAreRejectedBeforeRepositoryCreation()
    {
        await using var f = new NativeCloudPayloadTests.Fixture(); await f.InitializeAsync();
        var bundle = await ExportAsync(f); var service = new HistoryMigrationBundleService();
        var preview = await service.PreviewAsync(bundle);
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update))
        {
            var name = preview.History.Projects.Single().Migration!.NativeObjects.First().EntryName;
            var entry = zip.GetEntry(name)!; entry.Delete(); await using var output = zip.CreateEntry(name).Open(); await output.WriteAsync(new byte[] { 1 });
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(bundle));
        var target = Path.Combine(f.Root, "untouched");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new HistoryRepositoryBatchTransferService().ImportAsync(bundle, target));
        Assert.IsFalse(Directory.Exists(target));
        bundle = await ExportAsync(f);
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; JsonObject manifest;
            using (var input = entry.Open()) manifest = JsonNode.Parse(input)!.AsObject();
            entry.Delete(); manifest["projects"]![0]!["migration"]!["nativePayloads"]![0]!["sourceId"] = Guid.NewGuid().ToString();
            using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open()); output.Write(manifest.ToJsonString());
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(bundle));
    }

    [TestMethod]
    public async Task MetadataSnapshotDoesNotRequireALocalNativeStoreAndFailedDeepNeverReplacesThePriorPackage()
    {
        await using var f = new NativeCloudPayloadTests.Fixture(); await f.InitializeAsync();
        var bundle = await ExportAsync(f, embedded: false);
        Assert.AreEqual(3, (await new HistoryMigrationBundleService().PreviewAsync(bundle)).History.FormatVersion);
        var previous = File.ReadAllBytes(bundle);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ExportAsync(f, probe: new DeepProbe { Reject = true }));
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(bundle));
    }

    private sealed class DeepProbe : IPluginArtifactBackend
    {
        public int DeepCalls { get; private set; }
        public bool Reject { get; init; }
        public ValueTask<PluginArtifactProbeResult> ProbeAsync(Guid root, string plugin, CancellationToken token)
            => ValueTask.FromResult(new PluginArtifactProbeResult(true, true, MaterializationFidelity.Exact, "fixture"));
        public ValueTask<PayloadVerificationResult> VerifyAsync(Guid root, string plugin, CancellationToken token)
        { DeepCalls++; return ValueTask.FromResult(new PayloadVerificationResult(!Reject, "fixture source verification", "injected source mismatch")); }
        public ValueTask MaterializeAsync(Guid root, string plugin, string path, CancellationToken token) => throw new AssertFailedException();
    }
    private sealed class CoreArchive : IArchiveRepresentationBackend
    {
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
            => ValueTask.FromResult(new PayloadVerificationResult(true, "fixture archive contract", ""));
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string path, CancellationToken token)
            => throw new AssertFailedException();
    }
}
