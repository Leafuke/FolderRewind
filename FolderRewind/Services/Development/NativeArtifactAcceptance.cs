#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Cloud;
using FolderRewind.History.LocalState;
using FolderRewind.History.Retention;
using FolderRewind.History.Representation;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Loading;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Services.Development;

// Compiled only into a deliberately opted-in development build; no production command/UI.
internal static partial class NativeArtifactAcceptance
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    internal sealed record Request(string Workspace, string Phase, string Package, string PackageSha256,
        string SevenZip, string Source, string SourceId, bool Encrypted, string? Rclone = null, string? CrashAt = null,
        bool Incremental = false, int Iterations = 1, bool Lifecycle = false);
    private static string? _crashAt;
    internal static void Observe(string phase)
    {
        if (_crashAt == phase) System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
    internal static async Task<int> RunAsync(string requestPath)
    {
        var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(requestPath))!;
        var root = Path.GetFullPath(request.Workspace);
        if (!AppRuntimeInfo.IsMsiDistribution || !Path.IsPathFullyQualified(request.Workspace)) return 2;
        if (!BackupPathOverlapPolicy.Validate(request.Source, root).IsSafe) return 2;
        if (request.Phase == "capture")
        {
            if (Directory.Exists(root)) return 2;
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "acceptance-owner"), "native-artifact-p2");
        }
        else if (!File.Exists(Path.Combine(root, "acceptance-owner"))) return 2;
        Environment.SetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT", Path.Combine(root, "appdata"));
        try
        {
            ConfigService.Initialize();
            if (!ConfigService.ConfigDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Acceptance configuration escaped its workspace.");
            var started = DateTimeOffset.UtcNow;
            BackupConfig config;
            if (request.Phase == "capture")
            {
                var source = Path.Combine(root, "source");
                var before = await MergeTreeManifest.ReadAsync(request.Source, _ => true, default);
                foreach (var file in Directory.EnumerateFiles(request.Source, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked acceptance source.");
                    var target = Path.Combine(source, Path.GetRelativePath(request.Source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                }
                if ((await MergeTreeManifest.ReadAsync(request.Source, _ => true, default)).Digest != before.Digest
                    || (await MergeTreeManifest.ReadAsync(source, _ => true, default)).Digest != before.Digest)
                    throw new IOException("Input changed while creating the offline copy.");
                config = new BackupConfig { Name = "P2 isolated acceptance", DestinationPath = Path.Combine(root, "backup"),
                    Kind = new() { OwnerId = "com.folderrewind.minerewind", KindId = "minecraft-saves" },
                    RequiredPluginId = "com.folderrewind.minerewind", IsEncrypted = request.Encrypted,
                    ArtifactTransformPolicy = new() { Transformer = new() { PluginId = "com.folderrewind.minerewind", TransformerId = "minecraft-world-full" },
                        FailureBehavior = PersistedArtifactTransformFailureBehavior.RequireTransform } };
                config.Archive.Mode = request.Incremental ? BackupMode.Smart : BackupMode.Full; config.Archive.KeepCount = 0;
                if (request.Incremental) config.ArtifactTransformPolicy.Parameters["representation"] = JsonSerializer.SerializeToElement("incremental-v1");
                config.SourceFolders.Add(new ManagedFolder { Id = request.SourceId, Path = source, DisplayName = "world" });
                var saved = await ConfigService.UpdateAndSaveAsync(c => { c.BackupConfigs.Clear(); c.BackupConfigs.Add(config); c.GlobalSettings.SevenZipPath = request.SevenZip; });
                if (!saved.Success) throw new IOException(saved.ErrorMessage);
                if (request.Encrypted) EncryptionService.StorePassword(config.Id, "p2-isolated-acceptance-only");
            }
            else config = ConfigService.CurrentConfig.BackupConfigs.Single();
            var install = await new PluginPackageInstaller(Path.Combine(root, "plugins")).InstallAsync(request.Package,
                PluginInstallProvenance.Manual, request.PackageSha256);
            var manifest = install.Manifest.Contract;
            using var loaded = PluginAssemblyLoader.Load(new(manifest.PluginId, install.InstalledPath,
                manifest.EntryAssembly, manifest.EntryType, manifest.RequiredApi));
            var activation = await PluginV3RuntimeService.ActivateAsync(new(manifest.PluginId, () => loaded.Instance,
                new(manifest.PluginId, new Dictionary<string, JsonElement>()), [PluginV3ModelMapper.ToSnapshot(config)],
                new PluginV3HostServices(manifest.PluginId, Path.Combine(root, "plugin-data"), Path.Combine(root, "plugin-temp")),
                new PluginV3ActivationStore(), manifest));
            if (!activation.Success) throw new InvalidOperationException(JsonSerializer.Serialize(activation.Diagnostics));
            using var enabled = NativeHistoryArtifactTransformPolicy.EnterAcceptance(config.Id);
            _crashAt = request.Phase == "recover" ? null : request.CrashAt;
            if (request.Incremental) return await RunIncrementalAsync(request, config, root, started);
            if (request.Phase == "capture")
            {
                var result = await BackupService.BackupConfigAsync(config);
                if (!result.CreatedNewArchive) throw new InvalidDataException("Production BackupService did not create an archive: " + JsonSerializer.Serialize(result));
            }
            var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
            var representations = await history.Query.GetAllRepresentationsAsync();
            var versions = await history.Query.GetAllVersionsAsync();
            if (request.Phase == "recover")
            {
                bool committed = request.CrashAt is not null && (request.CrashAt.StartsWith("HistoryPackCommitted:")
                    || request.CrashAt.StartsWith("LocalStateApplied:") || request.CrashAt.StartsWith("Complete:"));
                if (versions.Count != (committed ? 1 : 0) || representations.Count != (committed ? 2 : 0))
                    throw new InvalidDataException("Crash recovery manufactured or lost committed History.");
                var native = NativeArtifactHostService.Store(config);
                if (native.Pending().Count != 0) throw new InvalidDataException("Crash recovery left pending capture intents.");
                await native.CollectAsync(representations.Where(r => r.Kind == RepresentationKind.PluginArtifact)
                    .Select(r => new ArtifactId(Guid.Parse(r.RepresentationSpecificMetadata["artifactRootId"]))), default);
                if (committed)
                {
                    if ((await history.Query.GetAllBranchUpdatesAsync()).Count != 1
                        || (await history.LocalReplicaCatalogStore.LoadAsync()).Value?.Entries.Length != 2)
                        throw new InvalidDataException("Recovery duplicated a Branch update or lost replica state.");
                    await new RepresentationRuntime([NativeArtifactHostService.Handler(config, history)]).MaterializeAsync(
                        representations.Single(r => r.Kind == RepresentationKind.PluginArtifact).RepresentationId, representations,
                        await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact,
                        Path.Combine(root, "recovered-world"));
                }
                await File.WriteAllTextAsync(Path.Combine(root, "recover-result.json"), JsonSerializer.Serialize(new {
                    Success = true, request.CrashAt, Committed = committed, Versions = versions.Count, Representations = representations.Count,
                    CoreOrphansMayRemain = !committed
                }));
                return 0;
            }
            if (versions.Count != 1 || representations.Count != 2 || representations.Any(r => r.VersionId != versions[0].VersionId))
                throw new InvalidDataException("Expected one Version with two representations.");
            var plugin = representations.Single(r => r.Kind == RepresentationKind.PluginArtifact);
            var core = representations.Single(r => r.Kind == RepresentationKind.CoreFull);
            var catalog = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!;
            var corePath = catalog.Entries.Single(e => e.RepresentationId == core.RepresentationId).Locator.AbsolutePath;
            if (request.Encrypted && request.Phase != "capture")
                await TransferAsync(request, config, history, plugin, core, corePath, upload: false);
            var engine = new RepresentationRuntime([NativeArtifactHostService.Handler(config, history)]);
            var restored = Path.Combine(root, request.Phase + "-plugin-restored");
            // Remove access to Core while reading the plugin representation; restore the control afterwards.
            File.Move(corePath, corePath + ".acceptance-held");
            try
            {
                var restore = new HistoryRestoreService(history, engine,
                    token => NativeHistoryApplicationService.BuildEnvironmentAsync(history, token), new FileSystemHistoryRestoreMutationBackend());
                var result = await restore.RestoreVersionsAsync(new Dictionary<SourceId, VersionId> { [versions[0].SourceId] = versions[0].VersionId },
                    [new(versions[0].SourceId, restored, versions[0].EffectiveSourceBoundary)],
                    (await history.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
                if (!result.Succeeded) throw new InvalidDataException("Plugin transaction restore failed: " + result.Diagnostic);
                var tip = (await history.Query.GetAllBranchUpdatesAsync()).Single();
                var checkout = await new HistoryCheckoutService(history, restore).CheckoutAsync(tip.UpdateId,
                    [new(versions[0].SourceId, Path.Combine(root, request.Phase + "-checkout"), versions[0].EffectiveSourceBoundary)],
                    (await history.WorkspaceStore.LoadAsync()).Value!, HistoryCheckoutProtectionMode.DiscardCurrentChanges);
                if (!checkout.Succeeded) throw new InvalidDataException("Plugin checkout failed: " + checkout.Diagnostic);
                using var exact = await restore.VerifyExactTreeAndLockAsync(versions[0], plugin.RepresentationSpecificMetadata["materializedTreeDigest"], default);
            }
            finally { File.Move(corePath + ".acceptance-held", corePath); }
            var control = Path.Combine(root, request.Phase + "-core-control");
            await new SevenZipHistoryArchiveBackend(config).MaterializeAsync([new(core, corePath)], control, default);
            var expected = await MergeTreeManifest.ReadAsync(control, _ => true, default);
            var actual = await MergeTreeManifest.ReadAsync(restored, _ => true, default);
            if (expected.Digest != actual.Digest) throw new InvalidDataException("Roundtrip bytes differ.");
            var coreStorageBytes = new FileInfo(corePath).Length;
            var artifactStorageBytes = (await NativeArtifactHostService.Store(config).Ledger.LoadAsync()).Artifacts.Sum(a => a.StorageSize);
            if (request.Phase == "workflow") await WorkflowAsync(config, history, engine, versions[0]);
            if (request.Phase == "faults") await CloudFaultsAsync(request, config, history, plugin);
            if (request.Encrypted && request.Phase == "capture")
            {
                await TransferAsync(request, config, history, plugin, core, corePath, upload: true);
                // Explicit destructive test confined to this newly-owned acceptance workspace.
                await NativeArtifactHostService.Store(config).CollectAsync([], default);
                File.Delete(corePath);
            }
            var ioAvailable = GetProcessIoCounters(System.Diagnostics.Process.GetCurrentProcess().Handle, out var io);
            var report = new {
                Phase = request.Phase, Success = true, Started = started, Ended = DateTimeOffset.UtcNow,
                Version = versions[0].VersionId, Core = core.RepresentationId, Plugin = plugin.RepresentationId,
                TreeDigest = actual.Digest, request.PackageSha256, request.Encrypted,
                CapturedFiles = expected.Files.Count, CapturedBytes = Directory.EnumerateFiles(control, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                ZeroByteRegions = Directory.EnumerateFiles(restored, "*.mca", SearchOption.AllDirectories).Count(f => new FileInfo(f).Length == 0),
                Diagnostics = versions[0].Diagnostics,
                CoreStorageBytes = coreStorageBytes, ArtifactStorageBytes = artifactStorageBytes,
                HostProcessIo = ioAvailable ? new { io.ReadBytes, io.WriteBytes, IncludesChildProcesses = false } : null,
                SafetySnapshotAndGenericMergeApply = request.Phase == "workflow",
                CloudFaults = request.Phase == "faults",
                ProductionRestore = true, PluginCheckout = true, GenericMergeExactInput = true,
                Cloud = request.Encrypted ? (request.Phase == "capture" ? "uploaded-local-remote-and-removed-local-payloads" : "downloaded-local-remote") : "not-requested",
                PeakWorkingSet = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64
            };
            await PluginV3RuntimeService.DeactivateAsync(manifest.PluginId);
            var defaultEngine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(new SevenZipHistoryArchiveBackend(config)),
                NativeArtifactHostService.Handler(config, history)]);
            if (!request.Encrypted || request.Phase != "capture")
            {
                var selection = await defaultEngine.AssessVersionAsync(versions[0].VersionId, representations,
                    await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), AssessmentDepth.Deep, MaterializationFidelity.Exact);
                if (selection.Selected?.RepresentationId != core.RepresentationId) throw new InvalidDataException("Disabled plugin did not fall back to Core.");
                try
                {
                    await engine.MaterializeAsync(plugin.RepresentationId, representations,
                        await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact, Path.Combine(root, "disabled-must-not-restore"));
                    throw new InvalidDataException("Explicit Plugin selection unexpectedly fell back.");
                }
                catch (InvalidOperationException) { }
            }
            if (request.Phase == "workflow")
            {
                config.Archive.KeepCount = 1;
                var cleanup = await NativeHistoryApplicationService.RunManualCleanupAsync(config.Id, config.ConfigRevision,
                    HistoryRetentionBenefitPolicy.CountFirst, null, default);
                if (cleanup?.Status is not ("Completed" or "NoWork")) throw new IOException("Native retention failed: " + JsonSerializer.Serialize(cleanup));
                var ledger = await NativeArtifactHostService.Store(config).Ledger.LoadAsync();
                if (ledger.Artifacts.Any(a => a.ArtifactId.Value.ToString("D") == plugin.RepresentationSpecificMetadata["artifactRootId"]))
                    throw new IOException("Released base Artifact was not reclaimed.");
                if (ledger.Artifacts.Count < 2) throw new IOException("Retention lost branch/SafetySnapshot Artifact roots.");
            }
            await File.WriteAllTextAsync(Path.Combine(root, request.Phase + "-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, request.Phase + "-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static async Task CloudFaultsAsync(Request request, BackupConfig config, HistoryRuntime history, VersionRepresentation plugin)
    {
        var conf = Path.Combine(request.Workspace, "fault-rclone.conf");
        await File.WriteAllTextAsync(conf, "[acceptance]\ntype = local\n");
        using var context = new RcloneExecutionContext(request.Rclone!, conf, request.Workspace,
            "acceptance:" + Path.Combine(request.Workspace, "fault-remote").Replace('\\', '/'), Path.Combine(request.Workspace, "rclone-tasks"));
        var real = new RcloneNativeHistoryTransport(context, 120);
        var fault = new FaultTransport(real) { InterruptUpload = true };
        var metadata = new HistoryMetadataSyncService(history, real);
        var replicas = new HistoryReplicaSyncService(history, fault, token => metadata.SyncAsync(token), new NoRetirement());
        var native = new NativeArtifactReplicaService(NativeArtifactHostService.Store(config), replicas);
        var interrupted = await native.UploadAsync(plugin, default);
        if (interrupted.Status != HistoryReplicaOperationStatus.Failed || (await history.Query.GetStorageReplicasAsync(plugin.RepresentationId)).Count != 0)
            throw new IOException("Interrupted upload advertised a recoverable Replica.");
        var retried = await native.UploadAsync(plugin, default);
        if (retried.Status != HistoryReplicaOperationStatus.Succeeded || retried.ReplicaId != interrupted.ReplicaId)
            throw new IOException("Upload retry lost its persistent identity.");
        var row = (await history.Query.GetStorageReplicasAsync(plugin.RepresentationId)).Single();
        var manifest = new HistoryReplicaManifest(plugin.RepresentationId, row.ReplicaId, Guid.Parse(plugin.RepresentationSpecificMetadata["artifactRootId"]),
            row.ObjectKey, row.ExpectedSize!.Value, row.ExpectedStorageSha256!);
        var revision = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision;
        fault.CorruptDownload = true;
        var corrupt = await native.DownloadAsync(plugin, manifest, config.Id, Guid.Parse(request.SourceId), default);
        if (corrupt.Status != HistoryReplicaOperationStatus.Failed || (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision != revision)
            throw new IOException("Corrupt download changed the local catalog.");
        var repaired = await native.DownloadAsync(plugin, manifest, config.Id, Guid.Parse(request.SourceId), default);
        if (repaired.Status != HistoryReplicaOperationStatus.Succeeded) throw new IOException("Download retry failed: " + repaired.Diagnostic);
    }

    private sealed class FaultTransport(IHistoryReplicaTransport inner) : IHistoryReplicaTransport
    {
        public bool InterruptUpload, CorruptDownload;
        public async Task UploadAsync(ReplicaId id, string path, CancellationToken token)
        { await inner.UploadAsync(id, path, token); if (InterruptUpload) { InterruptUpload = false; throw new IOException("Injected interruption after real upload."); } }
        public Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId id, CancellationToken token) => inner.VerifyRemoteAsync(id, token);
        public Task CommitManifestOnceAsync(HistoryReplicaManifest manifest, CancellationToken token) => inner.CommitManifestOnceAsync(manifest, token);
        public async Task DownloadAsync(HistoryReplicaManifest manifest, string path, CancellationToken token)
        { await inner.DownloadAsync(manifest, path, token); if (CorruptDownload) { CorruptDownload = false; await File.AppendAllTextAsync(path, "damage", token); } }
        public Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken token) => inner.DeletePhysicalAsync(manifest, token);
    }

    private static async Task WorkflowAsync(BackupConfig config, HistoryRuntime history, RepresentationRuntime engine, SourceVersion baseline)
    {
        var source = config.SourceFolders.Single().Path;
        var checkpoint = (await history.Query.GetAllCheckpointsAsync()).Single(c => c.VersionId == baseline.VersionId);
        var main = (await history.Query.GetAllBranchUpdatesAsync()).Single();
        var side = await history.Branches.CreateFromCheckpointAsync(checkpoint.CheckpointId, "p2-side");
        await File.WriteAllTextAsync(Path.Combine(source, "p2-safety.txt"), "safety snapshot data");
        var snapshot = await BackupService.CreateSafetySnapshotAsync(config, SafetySnapshotReason.BeforeMerge);
        if (snapshot.CheckpointIds.Length == 0) throw new InvalidDataException("SafetySnapshot is empty.");
        File.Delete(Path.Combine(source, "p2-safety.txt"));
        await File.WriteAllTextAsync(Path.Combine(source, "p2-ours.txt"), "ours independent change");
        if (!(await BackupService.BackupConfigAsync(config)).CreatedNewArchive) throw new IOException("Ours capture failed.");
        var restore = new HistoryRestoreService(history, engine, token => NativeHistoryApplicationService.BuildEnvironmentAsync(history, token), new FileSystemHistoryRestoreMutationBackend());
        var bindings = new[] { new HistoryRestoreSourceBinding(baseline.SourceId, source, baseline.EffectiveSourceBoundary) };
        var checkout = await new HistoryCheckoutService(history, restore).CheckoutAsync(side.BranchUpdate.UpdateId, bindings,
            (await history.WorkspaceStore.LoadAsync()).Value!, HistoryCheckoutProtectionMode.DiscardCurrentChanges);
        if (!checkout.Succeeded) throw new IOException(checkout.Diagnostic);
        await File.WriteAllTextAsync(Path.Combine(source, "p2-theirs.txt"), "theirs independent change");
        if (!(await BackupService.BackupConfigAsync(config)).CreatedNewArchive) throw new IOException("Theirs capture failed.");
        var session = await new HistoryMergeService(history, restore).StartAsync(main.BranchId, config.ConfigRevision, bindings)
            ?? throw new IOException("Expected a generic three-way merge.");
        if (session.State != MergeSessionState.Ready) throw new IOException("Generic merge did not become Ready: " + session.State);
        var archive = new SevenZipHistoryArchiveBackend(config);
        var builder = new HistoryMergeCommitBuilder(history, restore, archive, archive);
        var applied = await new HistoryMergeApplyService(history, restore, builder).ApplyAsync(session);
        if (!applied.Succeeded) throw new IOException("Generic merge failed: " + applied.Diagnostic);
        if (!File.Exists(Path.Combine(source, "p2-ours.txt")) || !File.Exists(Path.Combine(source, "p2-theirs.txt")))
            throw new IOException("Generic merge lost independent changes.");
        var workspace = (await history.WorkspaceStore.LoadAsync()).Value!;
        var output = (await history.Query.GetAllRepresentationsAsync()).Where(r => r.VersionId == workspace.GetSourceState(baseline.SourceId).BaseVersionId).ToArray();
        if (output.Length != 1 || output[0].Kind != RepresentationKind.CoreFull) throw new IOException("Generic Merge output must remain CoreFull.");
    }

    private static async Task TransferAsync(Request request, BackupConfig config, HistoryRuntime history,
        VersionRepresentation plugin, VersionRepresentation core, string corePath, bool upload)
    {
        var conf = Path.Combine(request.Workspace, "rclone.conf");
        if (!File.Exists(conf)) await File.WriteAllTextAsync(conf, "[acceptance]\ntype = local\n");
        using var context = new RcloneExecutionContext(request.Rclone ?? throw new InvalidOperationException("Rclone executable required."), conf,
            request.Workspace, "acceptance:" + Path.Combine(request.Workspace, "remote").Replace('\\', '/'), Path.Combine(request.Workspace, "rclone-tasks"));
        var transport = new RcloneNativeHistoryTransport(context, 120);
        var metadata = new HistoryMetadataSyncService(history, transport);
        var replicas = new HistoryReplicaSyncService(history, transport, token => metadata.SyncAsync(token), new NoRetirement());
        var native = new NativeArtifactReplicaService(NativeArtifactHostService.Store(config), replicas);
        if (upload)
        {
            var first = await native.UploadAsync(plugin, default);
            var retry = await native.UploadAsync(plugin, default);
            if (first.Status != HistoryReplicaOperationStatus.Succeeded || retry.Status != first.Status || first.ReplicaId != retry.ReplicaId)
                throw new IOException("Native Cloud upload/retry failed: " + first.Diagnostic + retry.Diagnostic);
            var result = await replicas.UploadAsync(core.RepresentationId, corePath);
            if (result.Status != HistoryReplicaOperationStatus.Succeeded) throw new IOException(result.Diagnostic);
        }
        else
        {
            foreach (var representation in new[] { plugin, core })
            {
                var replica = (await history.Query.GetStorageReplicasAsync(representation.RepresentationId)).Single();
                var manifest = new HistoryReplicaManifest(representation.RepresentationId, replica.ReplicaId,
                    representation.Kind == RepresentationKind.PluginArtifact ? Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]) : null,
                    replica.ObjectKey, replica.ExpectedSize!.Value, replica.ExpectedStorageSha256!);
                var result = representation.Kind == RepresentationKind.PluginArtifact
                    ? await native.DownloadAsync(representation, manifest, config.Id, Guid.Parse(request.SourceId), default)
                    : await replicas.DownloadAsync(manifest, corePath);
                if (result.Status != HistoryReplicaOperationStatus.Succeeded) throw new IOException(result.Diagnostic);
            }
        }
    }
    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    {
        public Task EnsureRetirementSafeAsync(StorageReplica replica, bool release, CancellationToken token)
            => throw new NotSupportedException();
    }
}
#endif
