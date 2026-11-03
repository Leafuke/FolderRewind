using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Migration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

/// <summary>On-demand, local-only observations. These never rewrite historical capture costs.</summary>
public sealed class HistoryBackupSizeService(HistoryRuntime runtime,
    Func<VersionRepresentation, string, CancellationToken, Task<IReadOnlyDictionary<string, long>?>> readArchive,
    Func<SourceVersion, VersionRepresentation, CancellationToken, Task<BackupSizeStatistics>> readNative)
{
    public async Task<BackupSizeStatistics> ReadAsync(VersionId versionId, RepresentationId representationId,
        BackupSizeStatistics known, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (known.LogicalContentBytes is not null && known.RestoreDependencyBytes is not null) return known;
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
        var graph = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
        if (version is null || !graph.TryGetValue(representationId, out var root) || root.VersionId != versionId) return known;
        try
        {
            BackupSizeStatistics observed;
            if (root.Kind == RepresentationKind.PluginArtifact)
                observed = await readNative(version, root, token).ConfigureAwait(false);
            else
                observed = await ReadArchivesAsync(root, graph, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(known.AddedStoredBytes, known.LogicalContentBytes ?? observed.LogicalContentBytes,
                known.RestoreDependencyBytes ?? observed.RestoreDependencyBytes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or FormatException or OverflowException or ArgumentException)
        { return known; }
    }

    private async Task<BackupSizeStatistics> ReadArchivesAsync(VersionRepresentation root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph, CancellationToken token)
    {
        var ordered = new List<VersionRepresentation>();
        var visited = new HashSet<RepresentationId>();
        var visiting = new HashSet<RepresentationId>();
        if (!Visit(root)) return BackupSizeStatistics.Unknown;
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var guards = new List<FileStream>();
        try
        {
            var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var listings = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            var countedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long stored = 0;
            foreach (var node in ordered)
            {
                token.ThrowIfCancellationRequested();
                var path = catalog?.Entries.Where(e => e.RepresentationId == node.RepresentationId
                    && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
                    .Select(e => e.Locator.AbsolutePath).FirstOrDefault(File.Exists);
                if (path is null) return BackupSizeStatistics.Unknown;
                // Hold the verified bytes against writes/replacement throughout listing and projection.
                var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                guards.Add(guard);
                if (node.RepresentationSpecificMetadata.TryGetValue("storageSha256", out var expected)
                    && !StringComparer.OrdinalIgnoreCase.Equals(expected, Convert.ToHexString(await SHA256.HashDataAsync(guard, token).ConfigureAwait(false))))
                    return BackupSizeStatistics.Unknown;
                var listing = await readArchive(node, path, token).ConfigureAwait(false);
                if (listing is null || listing.Values.Any(size => size < 0)) return BackupSizeStatistics.Unknown;
                if (countedPaths.Add(Path.GetFullPath(path))) stored = checked(stored + guard.Length);
                if (node.RepresentationSpecificMetadata.TryGetValue("legacyFileName", out var name)) listings.Add(name, listing);
                foreach (var file in listing) files[file.Key] = file.Value;
                if (node.RepresentationSpecificMetadata.TryGetValue("deletedFiles", out var deleted))
                    foreach (var file in deleted.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        files.Remove(file.Replace('\\', '/'));
            }
            if (root.RepresentationSpecificMetadata.TryGetValue(LegacySmartPlan.OwnersKey, out var ownerJson))
            {
                var owners = JsonSerializer.Deserialize<Dictionary<string, string>>(ownerJson)
                    ?? throw new InvalidDataException("Missing legacy ownership evidence.");
                files.Clear();
                foreach (var (path, name) in owners)
                {
                    if (!listings.TryGetValue(name, out var listing) || !listing.TryGetValue(path, out var size))
                        return BackupSizeStatistics.Unknown;
                    files.Add(path, size);
                }
            }
            if (root.RepresentationSpecificMetadata.TryGetValue(LegacySmartPlan.FilesKey, out var manifest)
                && !LegacySmartPlan.Paths(JsonSerializer.Deserialize<string[]>(manifest)).SetEquals(files.Keys))
                return BackupSizeStatistics.Unknown;
            return new(logicalContentBytes: files.Values.Sum(), restoreDependencyBytes: stored);
        }
        finally { foreach (var guard in guards) await guard.DisposeAsync().ConfigureAwait(false); }

        bool Visit(VersionRepresentation node)
        {
            if (visited.Contains(node.RepresentationId)) return true;
            if (node.Kind == RepresentationKind.PluginArtifact || !visiting.Add(node.RepresentationId)) return false;
            foreach (var id in node.DependencyRepresentationIds)
                if (!graph.TryGetValue(id, out var dependency) || !Visit(dependency)) return false;
            visiting.Remove(node.RepresentationId);
            visited.Add(node.RepresentationId);
            ordered.Add(node);
            return true;
        }
    }
}
