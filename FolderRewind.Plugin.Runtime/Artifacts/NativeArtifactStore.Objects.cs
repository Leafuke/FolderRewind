using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public interface INativeArtifactObjectTransport
{
    Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token);
    Task UploadObjectAsync(string key, string path, long size, string sha256, ArtifactTransferBudget budget, CancellationToken token);
    Task DownloadObjectAsync(string key, string path, long expectedLength, ArtifactTransferBudget budget, CancellationToken token);
}

public sealed record NativeArtifactObject(ArtifactLedgerEntry Node, NativeEnvelope Envelope);
public sealed record NativeArtifactObjectClosure(int Version, ArtifactId Root, IReadOnlyList<NativeArtifactObject> Objects);

public sealed partial class NativeArtifactStore
{
    public static string CloudObjectKey(ArtifactLedgerEntry node)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node.ConfigId))).ToLowerInvariant()
            + "/" + node.StorageSha256.ToLowerInvariant() + ".7z";

    // A durable transfer intent must already pin the root throughout this operation.
    public async Task ExportObjectsAsync(ArtifactId root, string descriptorPath, INativeArtifactObjectTransport transport, CancellationToken token)
    {
        NativeArtifactObject[] objects;
        using (await EnterAsync(token))
        {
            var ledger = await Ledger.LoadAsync(token);
            var map = ledger.Artifacts.ToDictionary(n => n.ArtifactId);
            var closure = NativeArtifactClosureLimits.Validate(ledger, root);
            objects = closure.TopologicallySorted.Select(id =>
            {
                var node = map[id];
                var envelopePath = Path.Combine(ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath), "envelope.json");
                if (new FileInfo(envelopePath).Length > 4096) throw new InvalidDataException("Oversized envelope descriptor.");
                var envelope = ArtifactMetadataFile.ReadJson<NativeEnvelope>(envelopePath, 4096) ?? throw new InvalidDataException();
                if (envelope.Version != 1) throw new InvalidDataException("Unsupported envelope.");
                return new NativeArtifactObject(node, envelope);
            }).ToArray();
        }
        var transferBudget = new ArtifactTransferBudget(ArtifactTransformLimits.Default.MaximumBytes);
        foreach (var item in objects)
        {
            token.ThrowIfCancellationRequested(); var node = item.Node;
            var key = CloudObjectKey(node);
            if (await transport.HasVerifiedObjectAsync(key, node.StorageSize, node.StorageSha256, token)) continue;
            var path = PayloadPath(node);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length != node.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexString(await SHA256.HashDataAsync(file, token)), node.StorageSha256))
                throw new InvalidDataException("Local ciphertext differs from committed Artifact.");
            await transport.UploadObjectAsync(key, path, node.StorageSize, node.StorageSha256, transferBudget, token);
        }
        var stable = objects.Select(o => o with { Node = o.Node with
        {
            ContentRelativePath = "artifacts/" + o.Node.ArtifactId.Value.ToString("N"),
            TransactionId = o.Node.ArtifactId.Value.ToString("N"), LocalAvailability = ArtifactAvailability.Available,
            CloudAvailability = ArtifactAvailability.Pending
        } }).ToArray();
        await WriteAsync(descriptorPath, new NativeArtifactObjectClosure(2, root, stable), token);
    }

    public async Task<string> ImportObjectsAsync(string descriptorPath, ArtifactId expectedRoot, string expectedStorageHash,
        string configId, Guid folderId, INativeArtifactObjectTransport transport, CancellationToken token)
    {
        if (new FileInfo(descriptorPath).Length > 16 * 1024 * 1024) throw new InvalidDataException("Closure descriptor quota exceeded.");
        var descriptor = ArtifactMetadataFile.ReadJson<NativeArtifactObjectClosure>(descriptorPath, 16 * 1024 * 1024)
            ?? throw new InvalidDataException("Empty closure descriptor.");
        if (descriptor.Version != 2 || descriptor.Root != expectedRoot || descriptor.Objects.Count > 4096
            || descriptor.Objects.Any(o => o.Node.ConfigId != configId || o.Node.FolderId != folderId || o.Envelope.Version != 1))
            throw new InvalidDataException("Invalid or foreign closure descriptor.");
        var graph = new ArtifactLedgerDocument(ArtifactLedgerValidator.CurrentSchemaVersion, new("cloud-objects"), descriptor.Objects.Select(o => o.Node).ToArray());
        ArtifactLedgerValidator.Validate(graph);
        if (NativeArtifactClosureLimits.Validate(graph, expectedRoot).Reachable.Count != graph.Artifacts.Count
            || !StringComparer.OrdinalIgnoreCase.Equals(graph.Artifacts.Single(n => n.ArtifactId == expectedRoot).StorageSha256, expectedStorageHash)
            || graph.Artifacts.Sum(n => n.StorageSize) > ArtifactTransformLimits.Default.MaximumBytes)
            throw new InvalidDataException("Closure does not match the committed root.");
        // Reuse the validated v1 import transaction. This temporary archive is restore-only,
        // never uploaded and never part of the incremental capture path.
        var work = Path.Combine(Root, "transfers", "objects-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var bundle = Path.Combine(work, "import.bundle");
        var transferBudget = new ArtifactTransferBudget(ArtifactTransformLimits.Default.MaximumBytes);
        try
        {
            using (ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(bundle,
                checked(graph.Artifacts.Sum(n => n.StorageSize) + graph.Artifacts.Count * 8192L + 16 * 1024 * 1024))))
            using (var output = new FileStream(bundle, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var item in descriptor.Objects)
                {
                    var file = Path.Combine(work, "object");
                    await transport.DownloadObjectAsync(CloudObjectKey(item.Node), file, item.Node.StorageSize, transferBudget, token);
                    await using (var input = File.OpenRead(file))
                    {
                        if (input.Length != item.Node.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(
                            Convert.ToHexString(await SHA256.HashDataAsync(input, token)), item.Node.StorageSha256))
                            throw new InvalidDataException("Downloaded ciphertext hash mismatch.");
                        input.Position = 0;
                        await using var entry = zip.CreateEntry(item.Node.ArtifactId.Value.ToString("N") + "/payload.7z", CompressionLevel.NoCompression).Open();
                        await input.CopyToAsync(entry, token);
                    }
                    File.Delete(file);
                    await using var metadata = zip.CreateEntry(item.Node.ArtifactId.Value.ToString("N") + "/envelope.json", CompressionLevel.NoCompression).Open();
                    await JsonSerializer.SerializeAsync(metadata, item.Envelope, cancellationToken: token);
                }
                await using var manifest = zip.CreateEntry("closure.json", CompressionLevel.NoCompression).Open();
                await JsonSerializer.SerializeAsync(manifest, new NativeArtifactClosure(1, expectedRoot, graph.Artifacts), cancellationToken: token);
            }
            return await ImportAsync(bundle, expectedRoot, expectedStorageHash, configId, folderId, token);
        }
        finally { DeleteDirectory(work); }
    }
}
