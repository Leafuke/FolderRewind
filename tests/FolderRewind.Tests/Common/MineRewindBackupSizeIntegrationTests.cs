using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed partial class MineRewindBackupSizeIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BundledSemanticFullSmartAndCheckpointMeasureFinalStoredData(bool encrypted)
    {
        var root = Path.Combine(Path.GetTempPath(), "MineRewindSizeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await CaptureAsync(root, encrypted); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task CaptureAsync(string root, bool encrypted)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var package = MineRewindIntegrationPackage.Resolve(Path.Combine(repo.FullName, "src", "FolderRewind"));
        var pluginPath = Path.Combine(root, "plugin"); ZipFile.ExtractToDirectory(package, pluginPath);
        var manifest = PluginPackageManifestReader.Parse(await File.ReadAllBytesAsync(Path.Combine(pluginPath, "manifest.json"))).Contract;
        var context = new PackageContext(pluginPath);
        IFolderRewindPlugin plugin;
        using (var assemblyInput = File.OpenRead(Path.Combine(pluginPath, manifest.EntryAssembly)))
            plugin = (IFolderRewindPlugin)Activator.CreateInstance(context.LoadFromStream(assemblyInput).GetType(manifest.EntryType, throwOnError: true)!)!;
        var sourcePath = Path.Combine(root, "source"); Directory.CreateDirectory(sourcePath);
        var stable = new byte[1024 * 1024]; new Random(771).NextBytes(stable);
        await File.WriteAllBytesAsync(Path.Combine(sourcePath, "stable.bin"), stable);
        await File.WriteAllBytesAsync(Path.Combine(sourcePath, "changed.bin"), new byte[1024]);
        var source = SourceId.New();
        var folder = new FolderSnapshot(source.Value, sourcePath, "synthetic world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var config = new ConfigSnapshot(Guid.NewGuid().ToString("D"), new("test"), manifest.ConfigKinds[0].Kind, "test", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        await using var temporary = await OwnedPluginTemporaryStorage.CreateAsync(Path.Combine(root, "temporary"));
        var runtime = new PluginRuntimeManager();
        var active = await runtime.ActivateAsync(new(manifest.PluginId, () => plugin, new(manifest.PluginId, new Dictionary<string, JsonElement>()),
            [config], new Host(temporary), new ActivationStore(), manifest));
        Assert.IsTrue(active.Success, JsonSerializer.Serialize(active.Diagnostics));
        var codec = new SevenZipArchiveProcessBackend(() => Path.Combine(repo.FullName, "7za.exe"), () => "size-test-only-password", encrypted, ".restore-marker");
        var store = new NativeArtifactStore(Path.Combine(root, "store"), codec);
        var service = new NativeArtifactIncrementalCaptureService(runtime, store, encrypted);
        var policy = new ArtifactTransformPolicy(manifest.ArtifactTransformers.Single(t => t.SupportsIncrementalInput).TransformerId,
            new Dictionary<string, JsonElement>(), ArtifactTransformFailureBehavior.RequireTransform);
        SourceCaptureBaseline? baseline = null;
        var previous = new HashSet<ArtifactId>();
        try
        {
            for (var step = 0; step < 3; step++)
            {
                if (step == 1) await File.WriteAllBytesAsync(Path.Combine(sourcePath, "changed.bin"), new byte[2048]);
                var files = Directory.GetFiles(sourcePath).ToImmutableSortedDictionary(path => Path.GetFileName(path)!,
                    path => new SourceCaptureFileState(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal);
                var plan = FileChangeCapturePlan.Create(files!, baseline, EffectiveSourceBoundarySnapshot.All, step > 0, requestCheckpoint: step == 2);
                var witness = new SourceCaptureBaselineCandidate(baseline?.Revision ?? -1, "", 0, files!);
                var capture = await service.CaptureAsync(plan, config, folder, policy, sourcePath, EffectiveSourceBoundarySnapshot.All, witness, default);
                try
                {
                    var graph = await store.Ledger.LoadAsync();
                    var rootId = new ArtifactId(Guid.Parse(capture.RepresentationCandidate!.Metadata["artifactRootId"]));
                    var closure = NativeArtifactClosureLimits.Validate(graph, rootId).Reachable;
                    long Bytes(ArtifactLedgerEntry node) => new FileInfo(store.PayloadPath(node)).Length
                        + new FileInfo(Path.Combine(Path.GetDirectoryName(store.PayloadPath(node))!, "envelope.json")).Length;
                    var expectedAdded = graph.Artifacts.Where(n => closure.Contains(n.ArtifactId) && !previous.Contains(n.ArtifactId)).Sum(Bytes);
                    var expectedClosure = graph.Artifacts.Where(n => closure.Contains(n.ArtifactId)).Sum(Bytes);
                    var snapshot = capture.VersionMetadataCandidates.Single();
                    Assert.AreEqual(expectedAdded, snapshot.Payload.GetProperty("addedStoredBytes").GetInt64());
                    Assert.AreEqual(files.Values.Sum(f => f.Size), snapshot.Payload.GetProperty("logicalContentBytes").GetInt64());
                    var expectedType = step == 1 ? HistoryBackupType.SemanticSmart : HistoryBackupType.SemanticFull;
                    Assert.AreEqual(expectedType.ToString(), snapshot.Payload.GetProperty("backupType").GetString());
                    Assert.AreEqual(expectedType, HistoryBackupTypePolicy.Resolve(null, capture.RepresentationCandidate.ToFact(capture.ReservedVersionId!.Value)));
                    Assert.AreEqual(expectedClosure, BackupSizeMetadata.ReadBytes(capture.RepresentationCandidate.Metadata, BackupSizeMetadata.NativeClosureBytesKey));
                    if (step == 1) Assert.IsGreaterThan(expectedAdded, expectedClosure, "Smart must reuse the unchanged data pack.");
                    else Assert.AreEqual(expectedAdded, expectedClosure, "Full/checkpoint output must be self-contained.");
                    var version = new SourceVersion(capture.ReservedVersionId!.Value, new(config.ConfigId), source, [], DateTimeOffset.UtcNow, null,
                        CaptureScope.FullSource, CaptureOutcome.Captured, [], new("world", sourcePath), capture.StateFingerprint, HistoryProvenance.Native("test"));
                    var observed = await NativeArtifactBackupSizeReader.ReadAsync(store, version, capture.RepresentationCandidate.ToFact(version.VersionId), default);
                    Assert.AreEqual(new BackupSizeStatistics(null, files.Values.Sum(f => f.Size), expectedClosure), observed);
                    if (step < 2)
                    {
                        var oldRepresentation = new VersionRepresentation(capture.RepresentationCandidate.RepresentationId, version.VersionId,
                            RepresentationKind.PluginArtifact, capture.RepresentationCandidate.Format, [], MaterializationFidelity.Exact,
                            null, capture.StateFingerprint, capture.RepresentationCandidate.Metadata.Remove(HistoryBackupTypePolicy.MetadataKey));
                        Assert.AreEqual(expectedType, await NativeArtifactBackupSizeReader.ReadTypeAsync(store, version, oldRepresentation, default));
                    }
                    baseline = new(source, step, version.VersionId, capture.RepresentationCandidate.RepresentationId, RepresentationKind.PluginArtifact,
                        capture.PayloadCandidate!.FileName, 0, files!, EffectiveSourceBoundarySnapshot.All.Fingerprint) { ArtifactRootId = rootId.Value };
                    previous = graph.Artifacts.Select(n => n.ArtifactId).ToHashSet();
                }
                finally { await capture.CommitParticipant!.AbortCommitAsync(default); }
            }
        }
        finally { await runtime.DeactivateAsync(manifest.PluginId); context.Unload(); }
    }

    // Stream loading keeps the exact bundled bytes while allowing deterministic Windows fixture cleanup.
    private sealed class PackageContext(string root) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IFolderRewindPlugin).Assembly.GetName().Name) return typeof(IFolderRewindPlugin).Assembly;
            var path = Path.Combine(root, name.Name + ".dll");
            if (!File.Exists(path)) return null;
            using var input = File.OpenRead(path);
            return LoadFromStream(input);
        }
    }

    private sealed class ActivationStore : IPluginActivationStore
    { public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token) => ValueTask.CompletedTask; }

    private sealed class Host(IPluginTemporaryStorage temporary) : IPluginHostServices, IPluginLogger, IReadOnlyConfigQueryService,
        IBackupRequestService, IRestoreRequestService, IHistoryQueryService, IPluginNotificationService, IKnotLinkHostService, IPluginDataStore
    {
        public IReadOnlyConfigQueryService Configs => this;
        public IBackupRequestService Backups => this;
        public IRestoreRequestService Restores => this;
        public IHistoryQueryService History => this;
        public IPluginNotificationService Notifications => this;
        public IKnotLinkHostService KnotLink => this;
        public IPluginDataStore DataStore => this;
        public IPluginTemporaryStorage TemporaryStorage => temporary;
        public IPluginLogger Logger => this;
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null) { }
        public bool IsAvailable => false;
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken token) => ValueTask.FromResult<ConfigSnapshot?>(null);
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken token) => throw new AssertFailedException();
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string versionId, CancellationToken token) => throw new AssertFailedException();
        public ValueTask<OperationOutcome> RequestQuickAsync(string configId, Guid folderId, CancellationToken token) => throw new AssertFailedException();
        public ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken token) => ValueTask.FromResult<IReadOnlyList<HistoryVersionSnapshot>>([]);
        public ValueTask ShowAsync(string title, string message, DiagnosticSeverity severity, CancellationToken token) => throw new AssertFailedException();
        public ValueTask SendAsync(string eventName, IReadOnlyDictionary<string, string> arguments, CancellationToken token) => throw new AssertFailedException();
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token) => throw new AssertFailedException();
        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken token) => throw new AssertFailedException();
    }
}
