using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public interface INativeArtifactObjectTransport
{
    // Reuse follows the transport's configured policy; it need not be a fresh content hash check.
    Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token);
    Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, string? localPath, CancellationToken token)
        => HasVerifiedObjectAsync(key, size, sha256, token);
    Task UploadObjectAsync(string key, string path, long size, string sha256, ArtifactTransferBudget budget, CancellationToken token);
    // Import staging holds the destination's space reservation; transports must not reserve it again.
    // The transport still enforces expectedLength and the cumulative transfer budget while writing.
    Task DownloadObjectAsync(string key, string path, long expectedLength, ArtifactTransferBudget budget, CancellationToken token);
}

public sealed record NativeArtifactObject(ArtifactLedgerEntry Node, NativeEnvelope Envelope);
public sealed record NativeArtifactObjectClosure(int Version, ArtifactId Root, IReadOnlyList<NativeArtifactObject> Objects);

public sealed partial class NativeArtifactStore
{
    public string? ExistingPayloadPath(ArtifactLedgerEntry node)
    {
        var path = PayloadPath(node);
        HostArtifactReadService.EnsureNoReparsePoints(Root, path);
        return File.Exists(path) ? path : null;
    }

    public static string CloudObjectKey(ArtifactLedgerEntry node)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node.ConfigId))).ToLowerInvariant()
            + "/" + node.StorageSha256.ToLowerInvariant() + ".7z";

    public static NativeArtifactObjectClosure ReadObjectClosure(string path, ArtifactId root, string storageHash,
        string configId, Guid folderId)
    {
        var descriptor = ArtifactMetadataFile.ReadJson<NativeArtifactObjectClosure>(path, 16 * 1024 * 1024)
            ?? throw new InvalidDataException("Empty closure descriptor.");
        return ValidateObjectClosure(descriptor, root, storageHash, configId, folderId);
    }

    public static NativeArtifactObjectClosure ValidateObjectClosure(NativeArtifactObjectClosure descriptor, ArtifactId root,
        string storageHash, string configId, Guid folderId)
    {
        if (descriptor.Version != 2 || descriptor.Root != root || descriptor.Objects is null || descriptor.Objects.Count > 4096
            || descriptor.Objects.Any(o => o is null || o.Node is null || o.Envelope is null
                || o.Node.ConfigId != configId || o.Node.FolderId != folderId || o.Envelope.Version != 1
                || o.Node.ContentRelativePath != "artifacts/" + o.Node.ArtifactId.Value.ToString("N")))
            throw new InvalidDataException("Invalid or foreign closure descriptor.");
        var graph = new ArtifactLedgerDocument(ArtifactLedgerValidator.CurrentSchemaVersion, new("cloud-objects"), descriptor.Objects.Select(o => o.Node).ToArray());
        ArtifactLedgerValidator.Validate(graph);
        if (NativeArtifactClosureLimits.Validate(graph, root).Reachable.Count != graph.Artifacts.Count
            || !StringComparer.OrdinalIgnoreCase.Equals(graph.Artifacts.Single(n => n.ArtifactId == root).StorageSha256, storageHash))
            throw new InvalidDataException("Closure does not match the committed root.");
        return descriptor;
    }

    public async Task WriteObjectDescriptorAsync(ArtifactId root, string descriptorPath, CancellationToken token)
    {
        using var held = await EnterAsync(token);
        var ledger = await Ledger.LoadAsync(token);
        var map = ledger.Artifacts.ToDictionary(n => n.ArtifactId);
        var closure = NativeArtifactClosureLimits.Validate(ledger, root);
        var objects = closure.TopologicallySorted.Select(id =>
        {
            var node = map[id];
            var envelopePath = Path.Combine(ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath), "envelope.json");
            HostArtifactReadService.EnsureNoReparsePoints(Root, envelopePath);
            var envelope = ArtifactMetadataFile.ReadJson<NativeEnvelope>(envelopePath, 4096) ?? throw new InvalidDataException();
            if (envelope.Version != 1) throw new InvalidDataException("Unsupported envelope.");
            return new NativeArtifactObject(node with
            {
                ContentRelativePath = "artifacts/" + node.ArtifactId.Value.ToString("N"),
                TransactionId = node.ArtifactId.Value.ToString("N"), LocalAvailability = ArtifactAvailability.Available,
                CloudAvailability = ArtifactAvailability.Pending
            }, envelope);
        }).ToArray();
        await WriteAsync(descriptorPath, new NativeArtifactObjectClosure(2, root, objects), token);
    }

    // The caller pins the closure before preparing or publishing its immutable descriptor.
    public async Task PublishObjectsAsync(string descriptorPath, ArtifactId root, string storageHash, string configId,
        Guid folderId, INativeArtifactObjectTransport transport, CancellationToken token)
    {
        var objects = ReadObjectClosure(descriptorPath, root, storageHash, configId, folderId).Objects;
        var transferBudget = new ArtifactTransferBudget(ArtifactTransformLimits.Default.MaximumBytes);
        foreach (var item in objects)
        {
            token.ThrowIfCancellationRequested(); var node = item.Node;
            var key = CloudObjectKey(node);
            var path = PayloadPath(node);
            if (await transport.HasVerifiedObjectAsync(key, node.StorageSize, node.StorageSha256,
                ExistingPayloadPath(node), token)) continue;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length != node.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexString(await SHA256.HashDataAsync(file, token)), node.StorageSha256))
                throw new InvalidDataException("Local ciphertext differs from committed Artifact.");
            await transport.UploadObjectAsync(key, path, node.StorageSize, node.StorageSha256, transferBudget, token);
        }
    }

    public async Task ExportObjectsAsync(ArtifactId root, string descriptorPath, INativeArtifactObjectTransport transport, CancellationToken token)
    {
        var temporary = descriptorPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await WriteObjectDescriptorAsync(root, temporary, token);
            var descriptor = ArtifactMetadataFile.ReadJson<NativeArtifactObjectClosure>(temporary, 16 * 1024 * 1024)!;
            var node = descriptor.Objects.Single(o => o.Node.ArtifactId == root).Node;
            await PublishObjectsAsync(temporary, root, node.StorageSha256, node.ConfigId, node.FolderId, transport, token);
            File.Move(temporary, descriptorPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<bool> AreObjectsAvailableAsync(NativeArtifactObjectClosure descriptor,
        INativeArtifactObjectTransport transport, CancellationToken token, Func<ArtifactLedgerEntry, string?>? localPath = null)
    {
        foreach (var item in descriptor.Objects)
            if (!await transport.HasVerifiedObjectAsync(CloudObjectKey(item.Node), item.Node.StorageSize, item.Node.StorageSha256,
                localPath?.Invoke(item.Node), token)) return false;
        return true;
    }

    public async Task<string> ImportObjectsAsync(string descriptorPath, ArtifactId expectedRoot, string expectedStorageHash,
        string configId, Guid folderId, INativeArtifactObjectTransport transport, CancellationToken token)
    {
        var descriptor = ReadObjectClosure(descriptorPath, expectedRoot, expectedStorageHash, configId, folderId);
        var graph = new ArtifactLedgerDocument(ArtifactLedgerValidator.CurrentSchemaVersion, new("cloud-objects"), descriptor.Objects.Select(o => o.Node).ToArray());
        var envelopes = descriptor.Objects.ToDictionary(o => o.Node.ArtifactId, o => o.Envelope);
        var transferBudget = new ArtifactTransferBudget(ArtifactTransformLimits.Default.MaximumBytes);
        return await ImportClosureAsync(new(1, expectedRoot, graph.Artifacts), expectedRoot, expectedStorageHash,
            configId, folderId, async (node, staging, allocation, cancellation) =>
            {
                await staging.WriteExternalFileAsync(allocation.Staging, "payload.7z", node.StorageSize,
                    (path, ct) => transport.DownloadObjectAsync(CloudObjectKey(node), path,
                        node.StorageSize, transferBudget, ct), cancellation);
                await using var metadata = await staging.OpenWriteAsync(allocation.Staging, "envelope.json", cancellation);
                await JsonSerializer.SerializeAsync(metadata, envelopes[node.ArtifactId], cancellationToken: cancellation);
            }, token);
    }
}
