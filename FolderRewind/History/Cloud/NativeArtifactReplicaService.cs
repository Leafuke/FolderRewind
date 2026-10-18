using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;
using System.Security.Cryptography;
using FolderRewind.History.Application;
using FolderRewind.History.Representation;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Cloud;

public sealed class NativeArtifactReplicaService
{
    private readonly NativeArtifactStore _store;
    private readonly HistoryReplicaSyncService _replicas;
    public NativeArtifactReplicaService(NativeArtifactStore store, HistoryReplicaSyncService replicas)
    { _store = store; _replicas = replicas; }
    public HistoryReplicaPayloadProtocol ForJob(HistoryRuntime history, HistoryCloudJob job,
        Func<RepresentationId, CancellationToken, Task<string?>> localPath, string? configId = null)
        => new PayloadProtocol(this, history, job, configId ?? history.ConfigId.Value, localPath);

    public async Task<HistoryReplicaOperationResult> UploadAsync(VersionRepresentation representation, HistoryRuntime history,
        HistoryCloudJob job, CancellationToken token, Func<HistoryReplicaManifest, CancellationToken, Task>? persistIntent = null, string? configId = null)
    {
        var replicaId = job.UploadIntents.Single(i => i.RepresentationId == representation.RepresentationId).ReplicaId;
        using var payload = await ForJob(history, job, (_, _) => Task.FromResult<string?>(null), configId)
            .PrepareAsync(representation, replicaId, token);
        persistIntent ??= async (manifest, ct) =>
        {
            await using var held = await history.MutationGate.EnterAsync(ct);
            var current = history.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId);
            var intent = current.UploadIntents.Single(i => i.RepresentationId == representation.RepresentationId);
            if (intent.Manifest is { } previous && !HistoryCloudReplicaValidity.Matches(manifest, previous))
                throw new InvalidDataException("[cloud.payload-changed] Native descriptor differs from the durable Job intent.");
            history.CloudJobs.Update(current with { UploadIntents = current.UploadIntents.Replace(intent, intent with { Manifest = manifest }) });
        };
        var result = await _replicas.UploadAsync(representation.RepresentationId, payload!.Path, Root(representation).Value,
            token, replicaId, persistIntent, payload.PublishAsync);
        if (result.Status == HistoryReplicaOperationStatus.Succeeded) await payload.CompleteAsync(token);
        return result;
    }

    private sealed class PayloadProtocol(NativeArtifactReplicaService owner, HistoryRuntime history, HistoryCloudJob job, string configId,
        Func<RepresentationId, CancellationToken, Task<string?>> localPath) : HistoryReplicaPayloadProtocol(localPath)
    {
        public override async Task<PreparedHistoryReplicaPayload?> PrepareAsync(VersionRepresentation representation, ReplicaId replicaId, CancellationToken token)
        {
            if (representation.Kind != RepresentationKind.PluginArtifact) return await base.PrepareAsync(representation, replicaId, token);
            RequireSupported(representation);
            if (!history.ConfigId.Matches(configId) || job.Protection != CloudProtectionDisposition.Held || job.ProjectId != history.ConfigId.Value
                || job.Target.ConfigId != history.ConfigId.Value
                || !job.UploadIntents.Any(i => i.RepresentationId == representation.RepresentationId && i.ReplicaId == replicaId))
                throw new InvalidDataException("Native transfer must belong to the durable Cloud Job intent.");
            var durable = history.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId);
            if (durable.Protection != CloudProtectionDisposition.Held || durable.Target != job.Target || durable.PackId != job.PackId
                || !durable.UploadIntents.Any(i => i.RepresentationId == representation.RepresentationId && i.ReplicaId == replicaId))
                throw new InvalidDataException("Native transfer differs from the durable Cloud Job.");
            var root = Root(representation);
            var id = "job-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(job.JobId))).ToLowerInvariant()
                + "-" + replicaId;
            var directory = Path.Combine(owner._store.Root, "transfers"); HostArtifactWorkspace.CreatePrivateDirectory(directory);
            var held = new FileStream(Path.Combine(directory, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            var path = Path.Combine(directory, id + ".json");
            var bundle = Path.Combine(directory, id + ".bundle");
            try
            {
                var expected = new NativeArtifactTransfer(id, root, representation.RepresentationId.ToString(), replicaId.ToString(), false)
                { JobId = job.JobId, TargetFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(job.Target))) };
                using (await owner._store.EnterAsync(token))
                {
                    if (File.Exists(path))
                    {
                        var previous = ArtifactMetadataFile.ReadJson<NativeArtifactTransfer>(path, 1024 * 1024);
                        if (previous is null || previous with { Complete = false } != expected)
                            throw new InvalidDataException("Native transfer conflicts with the frozen Cloud Job target or identity.");
                    }
                    await owner._store.SaveTransferAsync(expected, token);
                }
                await owner._store.WriteObjectDescriptorAsync(root, bundle, token);
                var version = await history.Query.GetVersionAsync(representation.VersionId, token)
                    ?? throw new InvalidDataException("Native source Version is missing.");
                return new(bundle, ct => owner._store.PublishObjectsAsync(bundle, root,
                    representation.RepresentationSpecificMetadata["storageSha256"], configId, version.SourceId.Value,
                    owner._replicas.ArtifactObjects ?? throw new InvalidOperationException("Artifact object transport is required."), ct),
                    async ct =>
                    {
                        using (await owner._store.EnterAsync(ct)) await owner._store.SaveTransferAsync(expected with { Complete = true }, ct);
                        File.Delete(bundle);
                    }, held.Dispose);
            }
            catch { held.Dispose(); throw; }
        }

        public override async Task<bool> CanReuseAsync(VersionRepresentation representation, HistoryReplicaManifest manifest,
            IHistoryReplicaTransport transport, CancellationToken token)
        {
            if (representation.Kind != RepresentationKind.PluginArtifact) return await base.CanReuseAsync(representation, manifest, transport, token);
            RequireSupported(representation);
            if (manifest.Size is < 1 or > 16 * 1024 * 1024 || transport is not INativeArtifactObjectTransport objects) return false;
            await using var workspaceOwner = await OwnedPluginTemporaryStorage.CreateAsync(Path.Combine(owner._store.Root, "transfer-workspaces"), token);
            var workspace = await workspaceOwner.CreateDirectoryAsync(token);
            var file = Path.Combine(workspace, "probe.bundle");
            try
            {
                await transport.DownloadAsync(manifest, file, token);
                await using (var input = File.OpenRead(file))
                    if (input.Length != manifest.Size || !StringComparer.OrdinalIgnoreCase.Equals(
                        Convert.ToHexString(await SHA256.HashDataAsync(input, token)), manifest.StorageSha256)) return false;
                var version = await history.Query.GetVersionAsync(representation.VersionId, token)
                    ?? throw new InvalidDataException("Native source Version is missing.");
                var descriptor = NativeArtifactStore.ReadObjectClosure(file, Root(representation),
                    representation.RepresentationSpecificMetadata["storageSha256"], configId, version.SourceId.Value);
                return await NativeArtifactStore.AreObjectsAvailableAsync(descriptor, objects, token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return false; }
            finally { HostArtifactWorkspace.DeleteOwnedDirectory(workspace); }
        }
    }

    public static Task CompleteJobAsync(NativeArtifactStore store, string jobId, CancellationToken token)
        => CompleteJobsAsync(store, [jobId], token);

    public static async Task CompleteJobsAsync(NativeArtifactStore store, System.Collections.Generic.IEnumerable<string> jobIds, CancellationToken token)
    {
        var completed = jobIds.ToHashSet(StringComparer.Ordinal);
        if (completed.Count == 0) return;
        var directory = Path.Combine(store.Root, "transfers");
        if (!Directory.Exists(directory)) return;
        using var held = await store.EnterAsync(token);
        foreach (var transfer in store.Transfers().Where(t => t.JobId is not null && completed.Contains(t.JobId)))
        {
            var path = Path.Combine(directory, transfer.Id + ".json");
            using (var operation = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // The caller has durably completed or explicitly abandoned a stopped Cloud Job.
                await store.SaveTransferAsync(transfer with { Complete = true }, token);
                var bundle = Path.ChangeExtension(path, ".bundle"); if (File.Exists(bundle)) File.Delete(bundle);
                File.Delete(path);
            }
            File.Delete(Path.ChangeExtension(path, ".lock"));
        }
    }

    public async Task<HistoryReplicaOperationResult> DownloadAsync(VersionRepresentation representation, HistoryReplicaManifest manifest,
        string configId, Guid folderId, CancellationToken token)
    {
        RequireSupported(representation);
        var root = Root(representation);
        if (manifest.ArtifactRootId != root.Value || manifest.RepresentationId != representation.RepresentationId)
            throw new InvalidDataException("Remote Artifact binding mismatch.");
        var id = "download-" + manifest.ReplicaId;
        var directory = Path.Combine(_store.Root, "transfers"); HostArtifactWorkspace.CreatePrivateDirectory(directory);
        using var gate = new FileStream(Path.Combine(directory, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var intent = new NativeArtifactTransfer(id, root, representation.RepresentationId.ToString(), manifest.ReplicaId.ToString(), false);
        var path = Path.Combine(directory, id + ".json");
        using (await _store.EnterAsync(token))
        {
            if (File.Exists(path))
            {
                var previous = ArtifactMetadataFile.ReadJson<NativeArtifactTransfer>(path, 1024 * 1024);
                if (previous is null || previous with { Complete = false } != intent) throw new InvalidDataException("Download intent mismatch.");
            }
            await _store.SaveTransferAsync(intent, token);
        }
        var bundle = Path.Combine(directory, id + ".bundle");
        var result = await _replicas.DownloadAsync(manifest, bundle, token,
            (file, ct) => ImportDescriptorAsync(file, representation, configId, folderId, ct));
        if (result.Status == HistoryReplicaOperationStatus.Succeeded)
        {
            using (await _store.EnterAsync(token)) await _store.SaveTransferAsync(intent with { Complete = true }, token);
            File.Delete(bundle);
            using (await _store.EnterAsync(token)) File.Delete(path);
            await ReleaseEquivalentStoppedDownloadsAsync(root, representation.RepresentationId, id, token);
        }
        return result;
    }

    private async Task ReleaseEquivalentStoppedDownloadsAsync(ArtifactId root, RepresentationId representation, string currentId, CancellationToken token)
    {
        // Successful import and catalog registration now protect this complete local realization.
        // Failed attempts through another replica no longer need their own retry roots.
        using var held = await _store.EnterAsync(token);
        foreach (var transfer in _store.Transfers().Where(t => t.JobId is null && t.Id != currentId
            && t.Id.StartsWith("download-", StringComparison.Ordinal) && t.Root == root && t.RepresentationId == representation.ToString()))
        {
            var path = Path.Combine(_store.Root, "transfers", transfer.Id + ".json");
            FileStream lease;
            try { lease = new(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose); }
            catch (IOException) { continue; } // A different download still owns its attempt.
            using (lease)
            {
                await _store.SaveTransferAsync(transfer with { Complete = true }, token);
                File.Delete(Path.ChangeExtension(path, ".bundle"));
                File.Delete(path);
            }
        }
    }

    private Task<string> ImportDescriptorAsync(string path, VersionRepresentation representation, string configId, Guid folderId, CancellationToken token)
    {
        var root = Root(representation);
        var descriptor = ArtifactMetadataFile.ReadJson<NativeArtifactObjectClosure>(path, 16 * 1024 * 1024)
            ?? throw new InvalidDataException("Missing Native descriptor.");
        var storedConfigId = descriptor.Objects?.SingleOrDefault(o => o.Node.ArtifactId == root)?.Node.ConfigId
            ?? throw new InvalidDataException("Missing Native root binding.");
        if (!new HistoryConfigId(configId).Matches(storedConfigId)) throw new InvalidDataException("Foreign Native configuration binding.");
        return _store.ImportObjectsAsync(path, root, representation.RepresentationSpecificMetadata["storageSha256"], storedConfigId, folderId,
            _replicas.ArtifactObjects ?? throw new InvalidOperationException("Artifact object transport is required."), token);
    }

    private static void RequireSupported(VersionRepresentation representation)
    {
        if (!NativePluginArtifactBackend.IsSupported(representation))
            throw new InvalidDataException("[history.unsupported-experimental-format] Native evidence v2 is required.");
    }
    private static ArtifactId Root(VersionRepresentation representation)
        => representation.Kind == RepresentationKind.PluginArtifact
            ? new(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]))
            : throw new InvalidOperationException("Native Artifact representation required.");
}
