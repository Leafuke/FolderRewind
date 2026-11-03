using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public static class NativeArtifactBackupSizeReader
{
    public static async Task<HistoryBackupType> ReadTypeAsync(NativeArtifactStore store, SourceVersion version,
        VersionRepresentation representation, CancellationToken token)
    {
        var declared = HistoryBackupTypePolicy.Resolve(null, representation);
        if (declared != HistoryBackupType.Semantic) return declared;
        if (!Guid.TryParse(representation.RepresentationSpecificMetadata.GetValueOrDefault("artifactRootId"), out var id)) return declared;
        var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
        var node = ledger.Artifacts.SingleOrDefault(n => n.ArtifactId.Value == id);
        if (node is null || node.FolderId != version.SourceId.Value || new HistoryConfigId(node.ConfigId) != version.ConfigId
            || !StringComparer.OrdinalIgnoreCase.Equals(node.StorageSha256, representation.RepresentationSpecificMetadata.GetValueOrDefault("storageSha256")))
            return declared;
        return node.CoreCaptureMode switch
        {
            CoreCaptureMode.Full => HistoryBackupType.SemanticFull,
            CoreCaptureMode.Smart => HistoryBackupType.SemanticSmart,
            _ => declared
        };
    }

    public static async Task<BackupSizeStatistics> ReadAsync(NativeArtifactStore store, SourceVersion version,
        VersionRepresentation representation, CancellationToken token)
    {
        if (!NativePluginArtifactBackend.IsSupported(representation)
            || !Guid.TryParse(representation.RepresentationSpecificMetadata.GetValueOrDefault("artifactRootId"), out var id))
            return BackupSizeStatistics.Unknown;
        var root = new ArtifactId(id);
        await using var read = await store.OpenLazyAsync(root, token).ConfigureAwait(false);
        var node = read.Nodes.Single(n => n.ArtifactId == root);
        if (node.FolderId != version.SourceId.Value || new HistoryConfigId(node.ConfigId) != version.ConfigId
            || node.Format.FormatId != representation.Format || !StringComparer.OrdinalIgnoreCase.Equals(
                node.StorageSha256, representation.RepresentationSpecificMetadata.GetValueOrDefault("storageSha256")))
            return BackupSizeStatistics.Unknown;
        long stored = 0;
        foreach (var item in read.Nodes.DistinctBy(n => n.ArtifactId))
        {
            var path = store.PayloadPath(item);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (input.Length != item.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(item.StorageSha256,
                Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))))
                return BackupSizeStatistics.Unknown;
            stored = checked(stored + input.Length + new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, "envelope.json")).Length);
        }
        var evidence = await IncrementalCaptureEvidence.ReadAsync(read, root, token).ConfigureAwait(false);
        if (evidence.VersionId != version.VersionId.ToString() || evidence.RepresentationId != representation.RepresentationId.ToString()
            || evidence.Fingerprint() != version.StateFingerprint)
            return BackupSizeStatistics.Unknown;
        return new(logicalContentBytes: evidence.Files.Sum(f => f.Length), restoreDependencyBytes: stored);
    }
}
