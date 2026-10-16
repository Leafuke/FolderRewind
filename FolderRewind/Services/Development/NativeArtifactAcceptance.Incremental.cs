#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Cloud;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private static async Task<int> RunIncrementalAsync(Request request, BackupConfig config, string root, DateTimeOffset started)
    {
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var measurements = new List<object>();
        if (request.Phase == "recover")
        {
            _crashAt = null;
            await (await NativeHistoryApplicationService.CreateRestoreServiceAsync(config)).RecoverIncompleteAsync();
            var versions = await history.Query.GetAllVersionsAsync();
            var representations = await history.Query.GetAllRepresentationsAsync();
            var committed = request.CrashAt is not null && (request.CrashAt.StartsWith("HistoryPackCommitted:")
                || request.CrashAt.StartsWith("LocalStateApplied:") || request.CrashAt.StartsWith("Complete:")
                || request.CrashAt.StartsWith("PrimaryCleanup:"));
            if (versions.Count != request.PreviousVersions + (committed ? 1 : 0) || representations.Count != versions.Count
                || representations.Any(r => r.Kind != RepresentationKind.PluginArtifact))
                throw new InvalidDataException("P3 crash recovery manufactured or lost committed History.");
            var store = NativeArtifactHostService.Store(config);
            if (store.Pending().Count != 0) throw new InvalidDataException("Pending capture survived recovery.");
            await store.CollectAsync(representations.Select(r => new FolderRewind.Plugin.Abstractions.ArtifactId(
                Guid.Parse(r.RepresentationSpecificMetadata["artifactRootId"]))), default);
            if (versions.Count > 0)
            {
                var restored = Path.Combine(root, "p3-recovered");
                var current = (await history.WorkspaceStore.LoadAsync()).Value!.SourceBaselines.Single().BaseVersionId;
                await new RepresentationRuntime([NativeArtifactHostService.Handler(config, history)]).MaterializeAsync(
                    representations.Single(r => r.VersionId == current).RepresentationId, representations,
                    await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact, restored);
                var expected = await MergeTreeManifest.ReadAsync(config.SourceFolders.Single().Path,
                    p => !p.Equals("session.lock", StringComparison.OrdinalIgnoreCase), default);
                var witness = Path.Combine(root, committed ? "p3-after-digest" : "p3-before-digest");
                var expectedDigest = File.Exists(witness) ? await File.ReadAllTextAsync(witness) : expected.Digest;
                if ((await MergeTreeManifest.ReadAsync(restored, _ => true, default)).Digest != expectedDigest)
                    throw new InvalidDataException("Recovered bytes differ.");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "p3-recover-result.json"), JsonSerializer.Serialize(new
            { Success = true, Committed = committed, Versions = versions.Count, request.CrashAt }));
            return 0;
        }
        if (request.Phase == "capture")
        {
            var result = await BackupService.BackupConfigAsync(config);
            if (!result.CreatedNewArchive) throw new InvalidDataException("Initial semantic capture failed: " + JsonSerializer.Serialize(result));
        }
        var source = config.SourceFolders.Single();
        if (request.Phase == "cache-loss")
        {
            var cached = await history.CaptureBaselines.LoadAsync(new(Guid.Parse(source.Id)));
            if (cached is not null) await history.CaptureBaselines.RemoveAsync(cached.SourceId, cached.Revision);
        }
        if (request.Phase == "plugin-missing")
        {
            var versionsBefore = (await history.Query.GetAllVersionsAsync()).Count;
            await PluginV3RuntimeService.Runtime.DeactivateAsync(new("com.folderrewind.minerewind"));
            var result = await BackupService.BackupConfigAsync(config);
            if (result.CreatedNewArchive || (await history.Query.GetAllVersionsAsync()).Count != versionsBefore)
                throw new InvalidDataException("Missing plugin advanced History.");
            await File.WriteAllTextAsync(Path.Combine(root, "p3-plugin-missing-result.json"), "{\"Success\":true}");
            return 0;
        }
        for (var i = 0; i < request.Iterations && request.Phase != "recover"; i++)
        {
            if (request.Phase != "capture" || i > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "p3-before-digest"),
                    (await MergeTreeManifest.ReadAsync(source.Path, p => p != "session.lock", default)).Digest);
                // Domain-neutral auxiliary file exercises real Smart add/change/delete without corrupting the test world.
                var file = Path.Combine(source.Path, "p3-verification.bin");
                if (i % 3 == 2 && File.Exists(file)) File.Delete(file);
                else { await File.WriteAllBytesAsync(file, new byte[] { 80, 51, (byte)i, 7 }); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(i + 1)); }
                await File.WriteAllTextAsync(Path.Combine(root, "p3-after-digest"),
                    (await MergeTreeManifest.ReadAsync(source.Path, p => p != "session.lock", default)).Digest);
                var before = (await NativeArtifactHostService.Store(config).Ledger.LoadAsync()).Artifacts.Sum(a => a.StorageSize);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = await BackupService.BackupConfigAsync(config);
                watch.Stop();
                if (!result.CreatedNewArchive) throw new InvalidDataException("Incremental production capture failed: " + JsonSerializer.Serialize(result));
                var after = (await NativeArtifactHostService.Store(config).Ledger.LoadAsync()).Artifacts.Sum(a => a.StorageSize);
                measurements.Add(new { Iteration = i, Milliseconds = watch.Elapsed.TotalMilliseconds, AddedStorageBytes = after - before });
            }
            var versions = await history.Query.GetAllVersionsAsync();
            var representations = await history.Query.GetAllRepresentationsAsync();
            if (representations.Count != versions.Count || representations.Any(r => r.Kind != RepresentationKind.PluginArtifact))
                throw new InvalidDataException("Incremental capture retained a Core control representation.");
            var workspace = (await history.WorkspaceStore.LoadAsync()).Value!;
            var current = workspace.SourceBaselines.Single().BaseVersionId!.Value;
            var selected = representations.Single(r => r.VersionId == current);
            var baseline = await history.CaptureBaselines.LoadAsync(new(Guid.Parse(source.Id)));
            if (baseline?.BaseRepresentationId != selected.RepresentationId || baseline.ArtifactRootId is null)
                throw new InvalidDataException("Capture cache does not bind the committed semantic representation.");
            var restored = Path.Combine(root, "p3-restored-" + Guid.NewGuid().ToString("N"));
            var engine = new RepresentationRuntime([NativeArtifactHostService.Handler(config, history)]);
            await engine.MaterializeAsync(selected.RepresentationId, representations,
                await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact, restored);
            var expected = await MergeTreeManifest.ReadAsync(source.Path, p => !p.Equals("session.lock", StringComparison.OrdinalIgnoreCase), default);
            var actual = await MergeTreeManifest.ReadAsync(restored, _ => true, default);
            if (expected.Digest != actual.Digest) throw new InvalidDataException("Incremental restore differs from source bytes.");
            var noChanges = await BackupService.BackupConfigAsync(config);
            if (noChanges.CreatedNewArchive) throw new InvalidDataException("No-change backup created a new representation.");
        }
        if (request.Lifecycle)
        {
            var versions = await history.Query.GetAllVersionsAsync();
            if (versions.Count != 1) throw new InvalidOperationException("Lifecycle fixture starts from exactly one committed Version.");
            await WorkflowAsync(config, history, new RepresentationRuntime([NativeArtifactHostService.Handler(config, history)]), versions.Single());
        }
        if (request.Rclone is not null)
        {
            var conf = Path.Combine(root, "p3-rclone.conf");
            await File.WriteAllTextAsync(conf, "[acceptance]\ntype = local\n");
            using var context = new RcloneExecutionContext(request.Rclone, conf, root,
                "acceptance:p3-remote", Path.Combine(root, "p3-rclone-tasks"));
            var transport = new RcloneNativeHistoryTransport(context, 120);
            var metadata = new HistoryMetadataSyncService(history, transport);
            var replicas = new HistoryReplicaSyncService(history, transport, token => metadata.SyncAsync(token), new NoRetirement());
            var native = new NativeArtifactReplicaService(NativeArtifactHostService.Store(config), replicas);
            var representations = (await history.Query.GetAllRepresentationsAsync()).Where(r => r.Kind == RepresentationKind.PluginArtifact).ToArray();
            foreach (var representation in representations)
            {
                var job = await PrepareAcceptanceCloudJobAsync(history, representation, context);
                var result = await native.UploadAsync(representation, history, job, default, configId: config.Id);
                if (result.Status != HistoryReplicaOperationStatus.Succeeded) throw new IOException(result.Diagnostic);
            }
            var selected = representations.Last();
            var replica = (await history.Query.GetStorageReplicasAsync(selected.RepresentationId)).Single();
            var manifest = new HistoryReplicaManifest(selected.RepresentationId, replica.ReplicaId,
                Guid.Parse(selected.RepresentationSpecificMetadata["artifactRootId"]), replica.ObjectKey,
                replica.ExpectedSize!.Value, replica.ExpectedStorageSha256!);
            var download = await native.DownloadAsync(selected, manifest, config.Id, Guid.Parse(request.SourceId), default);
            if (download.Status != HistoryReplicaOperationStatus.Succeeded) throw new IOException(download.Diagnostic);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "p3-" + request.Phase + "-result.json"), JsonSerializer.Serialize(new
        {
            Success = true, Started = started, Ended = DateTimeOffset.UtcNow, Measurements = measurements,
            Versions = (await history.Query.GetAllVersionsAsync()).Count,
            Representations = (await history.Query.GetAllRepresentationsAsync()).Count,
            PeakWorkingSet = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64,
            ColdCacheControlled = false, PerformanceGatePassed = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
#endif
