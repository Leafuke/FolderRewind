using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Cloud;

public sealed class NativeArtifactReplicaService(NativeArtifactStore store, HistoryReplicaSyncService replicas)
{
    public async Task<HistoryReplicaOperationResult> UploadAsync(VersionRepresentation representation, CancellationToken token)
    {
        var root = Root(representation);
        var id = "upload-" + representation.RepresentationId;
        var directory = Path.Combine(store.Root, "transfers"); Directory.CreateDirectory(directory);
        using var gate = new FileStream(Path.Combine(directory, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(directory, id + ".json");
        NativeArtifactTransfer intent;
        using (await store.EnterAsync(token))
        {
            intent = File.Exists(path) ? JsonSerializer.Deserialize<NativeArtifactTransfer>(await File.ReadAllTextAsync(path, token))!
                : new(id, root, representation.RepresentationId.ToString(), ReplicaId.New().ToString(), false);
            Validate(intent, id, root, representation);
            await NativeArtifactStore.WriteAsync(path, intent with { Complete = false }, token);
        }
        var bundle = Path.Combine(directory, id + ".bundle");
        var temp = bundle + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            if (representation.RepresentationSpecificMetadata.ContainsKey("incrementalEvidence"))
                await store.ExportObjectsAsync(root, temp, replicas.ArtifactObjects
                    ?? throw new InvalidOperationException("Cloud transport does not support incremental Artifact objects."), token);
            else await store.ExportAsync(root, temp, token);
            File.Move(temp, bundle, true);
            var result = await replicas.UploadAsync(representation.RepresentationId, bundle, root.Value, token,
                ReplicaId.Parse(intent.ReplicaId));
            if (result.Status == HistoryReplicaOperationStatus.Succeeded)
            {
                using (await store.EnterAsync(token)) await NativeArtifactStore.WriteAsync(path, intent with { Complete = true }, token);
                File.Delete(bundle);
            }
            return result;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<HistoryReplicaOperationResult> DownloadAsync(VersionRepresentation representation, HistoryReplicaManifest manifest,
        string configId, Guid folderId, CancellationToken token)
    {
        var root = Root(representation);
        if (manifest.ArtifactRootId != root.Value || manifest.RepresentationId != representation.RepresentationId)
            throw new InvalidDataException("Remote Artifact binding mismatch.");
        var id = "download-" + manifest.ReplicaId;
        var directory = Path.Combine(store.Root, "transfers"); Directory.CreateDirectory(directory);
        using var gate = new FileStream(Path.Combine(directory, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var intent = new NativeArtifactTransfer(id, root, representation.RepresentationId.ToString(), manifest.ReplicaId.ToString(), false);
        var path = Path.Combine(directory, id + ".json");
        using (await store.EnterAsync(token)) await NativeArtifactStore.WriteAsync(path, intent, token);
        var bundle = Path.Combine(directory, id + ".bundle");
        var result = await replicas.DownloadAsync(manifest, bundle, token,
            (file, ct) => representation.RepresentationSpecificMetadata.ContainsKey("incrementalEvidence")
                ? store.ImportObjectsAsync(file, root, representation.RepresentationSpecificMetadata["storageSha256"], configId, folderId,
                    replicas.ArtifactObjects ?? throw new InvalidOperationException("Cloud transport does not support incremental Artifact objects."), ct)
                : store.ImportAsync(file, root, representation.RepresentationSpecificMetadata["storageSha256"], configId, folderId, ct));
        if (result.Status == HistoryReplicaOperationStatus.Succeeded)
        {
            using (await store.EnterAsync(token)) await NativeArtifactStore.WriteAsync(path, intent with { Complete = true }, token);
            File.Delete(bundle);
        }
        return result;
    }

    private static ArtifactId Root(VersionRepresentation representation)
        => representation.Kind == RepresentationKind.PluginArtifact
            ? new(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]))
            : throw new InvalidOperationException("Native Artifact representation required.");
    private static void Validate(NativeArtifactTransfer? intent, string id, ArtifactId root, VersionRepresentation representation)
    {
        if (intent is null || intent.Id != id || intent.Root != root || intent.RepresentationId != representation.RepresentationId.ToString()
            || !Guid.TryParse(intent.ReplicaId, out _)) throw new InvalidDataException("Transfer intent mismatch.");
    }
}
