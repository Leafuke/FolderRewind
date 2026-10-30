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
using FolderRewind.History.Storage;
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
        bool Incremental = true, int Iterations = 1, bool Lifecycle = false, bool Benchmark = false, int PreviousVersions = 0,
        bool ExperimentalDelivery = false, string? MigrationBundle = null, string? BaselinePackage = null, bool TypedFilters = false);
    private static string? _crashAt;
    internal static Action<string>? Observation;
    internal static void Observe(string phase)
    {
        if (_crashAt == phase) System.Diagnostics.Process.GetCurrentProcess().Kill();
        Observation?.Invoke(phase);
    }
    internal static async Task<int> RunAsync(string requestPath)
    {
        var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(requestPath))!;
        var root = Path.GetFullPath(request.Workspace);
        if (!AppRuntimeInfo.IsMsiDistribution || !Path.IsPathFullyQualified(request.Workspace)) return 2;
        if (!BackupPathOverlapPolicy.Validate(request.Source, root).IsSafe) return 2;
        if (request.Phase is "capture" or "migration-import")
        {
            if (Directory.Exists(root)) return 2;
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "acceptance-owner"), "native-artifact-v2");
        }
        else if (!File.Exists(Path.Combine(root, "acceptance-owner"))) return 2;
        _crashAt = request.Phase.EndsWith("recover", StringComparison.Ordinal) || request.Phase == "migration-restart" ? null : request.CrashAt;
        Environment.SetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT", Path.Combine(root, "appdata"));
        try
        {
            ConfigService.Initialize();
            if (!ConfigService.ConfigDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Acceptance configuration escaped its workspace.");
            var started = DateTimeOffset.UtcNow;
            BackupConfig config;
            if (request.Phase is "capture" or "migration-import")
            {
                var source = Path.Combine(root, "source");
                var before = await MergeTreeManifest.ReadAsync(request.Source, _ => true, default);
                var pending = new Stack<string>(); pending.Push(request.Source);
                while (pending.TryPop(out var directory))
                {
                    Directory.CreateDirectory(Path.Combine(source, Path.GetRelativePath(request.Source, directory)));
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked acceptance source.");
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else File.Copy(entry, Path.Combine(source, Path.GetRelativePath(request.Source, entry)));
                    }
                }
                if ((await MergeTreeManifest.ReadAsync(request.Source, _ => true, default)).Digest != before.Digest
                    || (await MergeTreeManifest.ReadAsync(source, _ => true, default)).Digest != before.Digest)
                    throw new IOException("Input changed while creating the offline copy.");
                if (request.Phase == "migration-import")
                {
                    var saved = await ConfigService.UpdateAndSaveAsync(c => c.GlobalSettings.SevenZipPath = request.SevenZip);
                    if (!saved.Success) throw new IOException(saved.ErrorMessage);
                    var imported = await ImportMigrationFixtureAsync(request, root);
                    if (imported is null) return 0;
                    config = imported;
                }
                else
                {
                config = new BackupConfig { Name = "Native v2 isolated acceptance", DestinationPath = Path.Combine(root, "backup"),
                    Kind = new() { OwnerId = "com.folderrewind.minerewind", KindId = "minecraft-saves" },
                    RequiredPluginId = "com.folderrewind.minerewind", IsEncrypted = request.Encrypted,
                    ArtifactTransformPolicy = new() { Transformer = new() { PluginId = "com.folderrewind.minerewind", TransformerId = "minecraft-world-full" },
                        FailureBehavior = PersistedArtifactTransformFailureBehavior.RequireTransform, ExperimentalEnabled = !request.ExperimentalDelivery } };
                config.Archive.Mode = request.Incremental ? BackupMode.Smart : BackupMode.Full; config.Archive.KeepCount = 0;
                config.ArtifactTransformPolicy.Parameters["representation"] = JsonSerializer.SerializeToElement("incremental-v1");
                if (request.Benchmark)
                {
                    config.Archive.Mode = BackupMode.Smart;
                    config.Archive.MaxSmartBackupsPerFull = 0;
                    if (!request.Incremental) config.ArtifactTransformPolicy = null;
                }
                config.SourceFolders.Add(new ManagedFolder { Id = request.SourceId, Path = source, DisplayName = "world" });
                var saved = await ConfigService.UpdateAndSaveAsync(c => { c.BackupConfigs.Clear(); c.BackupConfigs.Add(config); c.GlobalSettings.SevenZipPath = request.SevenZip; });
                if (!saved.Success) throw new IOException(saved.ErrorMessage);
                if (request.Encrypted) EncryptionService.StorePassword(config.Id, "p2-isolated-acceptance-only");
                }
            }
            else config = ConfigService.CurrentConfig.BackupConfigs.Single();
            if (request.Phase == "ui-upgrade-seed")
            {
                var oldPackage = request.BaselinePackage ?? throw new IOException("Baseline package is required.");
                var oldSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(oldPackage)));
                await new PluginPackageInstaller(PluginV3PackageService.PluginsRoot).InstallAsync(oldPackage, PluginInstallProvenance.BundledOfficial, oldSha);
                var saved = await ConfigService.UpdateAndSaveAsync(c =>
                {
                    c.GlobalSettings.Plugins.EnabledIntent["com.folderrewind.minerewind"] = true;
                    c.GlobalSettings.Plugins.AutoCheckUpdates = false;
                    c.BackupConfigs.Single().ArtifactTransformPolicy!.ExperimentalEnabled = false;
                });
                if (!saved.Success) throw new IOException(saved.ErrorMessage);
                await File.WriteAllTextAsync(Path.Combine(root, "ui-upgrade-seed-result.json"), "{\"Success\":true}");
                return 0;
            }
            var packageIdentity = Convert.ToHexString(Convert.FromHexString(request.PackageSha256));
            if (packageIdentity.Length != 64) throw new InvalidDataException("Invalid acceptance package SHA.");
            var install = await new PluginPackageInstaller(request.TypedFilters ? PluginV3PackageService.PluginsRoot : Path.Combine(root, "plugins", packageIdentity)).InstallAsync(request.Package,
                PluginInstallProvenance.Manual, request.PackageSha256);
            var manifest = install.Manifest.Contract;
            using var loaded = PluginAssemblyLoader.Load(new(manifest.PluginId, install.InstalledPath,
                manifest.EntryAssembly, manifest.EntryType, manifest.RequiredApi));
            await File.WriteAllTextAsync(Path.Combine(root, "temporary-scope"), Plugin.Runtime.Artifacts.OwnedPluginTemporaryStorage.ForLogicalRoot(
                Path.Combine(root, "plugin-temp", manifest.PluginId.Value)));
            var activation = await PluginV3RuntimeService.ActivateAsync(new(manifest.PluginId, () => loaded.Instance,
                new(manifest.PluginId, new Dictionary<string, JsonElement>()), [PluginV3ModelMapper.ToSnapshot(config)],
                new PluginV3HostServices(manifest.PluginId, Path.Combine(root, "plugin-data"), Path.Combine(root, "plugin-temp")),
                new PluginV3ActivationStore(), manifest));
            if (!activation.Success) throw new InvalidOperationException(JsonSerializer.Serialize(activation.Diagnostics));
            if (request.TypedFilters) return await RunTypedFilterMergeAsync(config, root);
            if (request.ExperimentalDelivery && request.Phase == "capture") return await RunExperimentalDeliveryAsync(config, root);
            if (request.Benchmark) return await RunBenchmarkAsync(request, config, root);
            if (request.Phase is "cloud" or "cloud-recover") return await RunCloudIntegrationAsync(request, config, root);
            if (request.Phase == "app-services") return await RunApplicationServicesAsync(config, root);
            if (request.Phase == "migration-export") return await RunMigrationExportAsync(config, root);
            if (request.Phase is "migration-import" or "migration-restart") return await RunMigrationRecoveryAsync(config, root, request.Phase == "migration-restart");
            return await RunIncrementalAsync(request, config, root, started);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, request.Phase + "-error.txt"), ex.ToString() + "\n" + string.Join("\n", LogService.GetEntriesSnapshot().Select(LogService.FormatEntry)));
            return 1;
        }
    }

    private static async Task<HistoryCloudJob> PrepareAcceptanceCloudJobAsync(HistoryRuntime history,
        VersionRepresentation representation, RcloneExecutionContext context)
    {
        var pack = (await history.Repository.ReadAllPacksAsync()).First(p => p.Pack.Objects.Any(o =>
            o.Kind == HistoryObjectKinds.VersionRepresentation && o.Id == representation.RepresentationId.ToString())).Pack;
        await using var gate = await history.MutationGate.EnterAsync(default);
        var job = history.CloudJobs.RegisterManual(pack, new("acceptance", "fixture", "binding", context.RemoteRoot, history.ConfigId.Value),
            [representation.RepresentationId]);
        if (job.UploadIntents.IsEmpty)
        {
            job = job with { UploadIntents = [new(representation.RepresentationId, ReplicaId.New(), null)] };
            history.CloudJobs.Update(job);
        }
        return job;
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

    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    {
        public Task EnsureRetirementSafeAsync(StorageReplica replica, bool release, CancellationToken token)
            => throw new NotSupportedException();
    }
}
#endif
