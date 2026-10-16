using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

/// <summary>Binds a previously Deep-verified realization to all immutable closure inputs, including shared dependencies.</summary>
public static class NativeArtifactMaterializationEvidence
{
    public static async Task<string> CaptureAsync(HistoryConfigId config, NativeArtifactStore store,
        IReadOnlyList<VersionRepresentation> representations, IReadOnlyList<SourceVersion> versions,
        IEnumerable<LocalReplicaCatalogEntry> catalog, CancellationToken token)
    {
        var registered = catalog.Select(e => e.RepresentationId).ToHashSet();
        var selected = representations.Where(r => registered.Contains(r.RepresentationId) && NativePluginArtifactBackend.IsSupported(r))
            .OrderBy(r => r.RepresentationId.ToString(), StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return "";
        using var held = await store.EnterAsync(token).ConfigureAwait(false);
        var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
        var roots = selected.Select(r => new ArtifactId(Guid.Parse(r.RepresentationSpecificMetadata["artifactRootId"]))).ToArray();
        var closure = NativeArtifactClosureLimits.Validate(ledger, roots);
        var map = ledger.Artifacts.ToDictionary(n => n.ArtifactId);
        var stamp = new StringBuilder(store.Root).Append('\0');
        foreach (var representation in selected)
        {
            var root = new ArtifactId(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]));
            var node = map[root]; var source = versions.Single(v => v.VersionId == representation.VersionId).SourceId;
            if (!config.Matches(node.ConfigId) || node.FolderId != source.Value
                || !StringComparer.OrdinalIgnoreCase.Equals(node.StorageSha256, representation.RepresentationSpecificMetadata.GetValueOrDefault("storageSha256")))
                throw new InvalidDataException("Native recovery evidence conflicts with History identity.");
            var owned = NativeArtifactClosureLimits.Validate(ledger, root);
            if (owned.Reachable.Any(id => map[id].ConfigId != node.ConfigId || map[id].FolderId != node.FolderId))
                throw new InvalidDataException("Native recovery evidence crosses a source boundary.");
            stamp.Append(representation.RepresentationId).Append(':').Append(root).Append(';');
        }
        foreach (var id in closure.TopologicallySorted)
        {
            token.ThrowIfCancellationRequested(); var node = map[id]; var payload = store.PayloadPath(node);
            HistoryReplicaRecoveryService.RequireRegularPath(store.Root, payload);
            var metadata = Path.Combine(Path.GetDirectoryName(payload)!, "envelope.json");
            HistoryReplicaRecoveryService.RequireRegularPath(store.Root, metadata);
            var envelope = ArtifactMetadataFile.ReadJson<NativeEnvelope>(metadata, 4096);
            if (envelope is not { Version: 1 }) throw new InvalidDataException("Invalid Native recovery envelope.");
            await using var input = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (input.Length != node.StorageSize) throw new InvalidDataException("Native recovery dependency length changed.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            if (input.Length != node.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(hash, node.StorageSha256))
                throw new InvalidDataException("Native recovery dependency bytes changed.");
            stamp.Append(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node)))).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope)))).Append(':').Append(hash).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
    }
}
