using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace FolderRewind.History.Domain;

/// <summary>Capture costs belong to the Version; dependency costs belong to its selected Representation.</summary>
public sealed record BackupSizeStatistics
{
    public BackupSizeStatistics(long? addedStoredBytes = null, long? logicalContentBytes = null, long? restoreDependencyBytes = null)
    {
        if (addedStoredBytes < 0 || logicalContentBytes < 0 || restoreDependencyBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(addedStoredBytes), "Backup sizes cannot be negative.");
        AddedStoredBytes = addedStoredBytes;
        LogicalContentBytes = logicalContentBytes;
        RestoreDependencyBytes = restoreDependencyBytes;
    }

    public long? AddedStoredBytes { get; }
    public long? LogicalContentBytes { get; }
    public long? RestoreDependencyBytes { get; }
    public static BackupSizeStatistics Unknown { get; } = new();
}

public static class BackupSizeMetadata
{
    public const string ProducerId = "folderrewind.core";
    public const string SchemaId = "backup-size";
    public const int SchemaVersion = 1;
    public const string StoredBytesKey = "hostStoredBytes";
    public const string NativeClosureBytesKey = "hostRestoreDependencyBytes";

    public static bool IsCaptureStatistics(string producerId, string schemaId)
        => producerId == ProducerId && schemaId == SchemaId;

    public static JsonElement CapturePayload(long addedStoredBytes, long logicalContentBytes, HistoryBackupType backupType = HistoryBackupType.Unknown)
    {
        _ = new BackupSizeStatistics(addedStoredBytes, logicalContentBytes);
        return backupType == HistoryBackupType.Unknown
            ? JsonSerializer.SerializeToElement(new { addedStoredBytes, logicalContentBytes })
            : JsonSerializer.SerializeToElement(new { addedStoredBytes, logicalContentBytes, backupType = backupType.ToString() });
    }

    public static BackupSizeStatistics ReadCapture(VersionMetadataSnapshot? snapshot)
        => snapshot is not null && IsCaptureStatistics(snapshot.ProducerPluginId, snapshot.SchemaId)
            && snapshot.SchemaVersion == SchemaVersion && snapshot.Payload.ValueKind == JsonValueKind.Object
            ? new(ReadNumber(snapshot.Payload, "addedStoredBytes"), ReadNumber(snapshot.Payload, "logicalContentBytes"))
            : BackupSizeStatistics.Unknown;

    public static long? ReadBytes(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0 ? bytes : null;

    public static string FormatBytes(long bytes)
        => bytes >= 0 ? bytes.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(bytes));

    private static long? ReadNumber(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null;

    /// <summary>Only immutable storage facts are used here; browsing must never open backup content.</summary>
    public static long? DependencyBytes(VersionRepresentation? root, IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
        => DependencyBytes(root, graph, CancellationToken.None);

    internal static long? DependencyBytes(VersionRepresentation? root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph, CancellationToken token)
        => new DependencySizeContext(graph).GetBytes(root, token);

    // Scoped to one immutable query graph; completed suffixes can be shared by single-parent chains.
    internal sealed class DependencySizeContext(IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
    {
        private readonly Dictionary<RepresentationId, long?> _completed = [];

        internal long? GetBytes(VersionRepresentation? root, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (root is null) return null;
            if (_completed.TryGetValue(root.RepresentationId, out var known)) return known;
            var pending = new Stack<(RepresentationId Id, long Bytes)>();
            var visiting = new HashSet<RepresentationId>();
            var node = root;
            long? total;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_completed.TryGetValue(node.RepresentationId, out total)) break;
                if (!visiting.Add(node.RepresentationId)) { total = null; break; }
                var bytes = ReadBytes(node.RepresentationSpecificMetadata,
                    node.Kind == RepresentationKind.PluginArtifact ? NativeClosureBytesKey : StoredBytesKey);
                if (bytes is null)
                {
                    _completed[node.RepresentationId] = total = null;
                    break;
                }
                if (node.DependencyRepresentationIds.Length > 1)
                {
                    // Summing cached subtree totals would count shared ancestors more than once.
                    total = TraverseDependencyBytes(node, graph, token);
                    token.ThrowIfCancellationRequested();
                    _completed[node.RepresentationId] = total;
                    break;
                }
                pending.Push((node.RepresentationId, bytes.Value));
                if (node.DependencyRepresentationIds.IsEmpty) { total = 0; break; }
                var found = graph.TryGetValue(node.DependencyRepresentationIds[0], out var dependency);
                token.ThrowIfCancellationRequested();
                if (!found) { total = null; break; }
                node = dependency!;
            }
            while (pending.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                try { total = total is { } value ? checked(value + frame.Bytes) : null; }
                catch (OverflowException) { total = null; }
                _completed[frame.Id] = total;
            }
            token.ThrowIfCancellationRequested();
            return total;
        }
    }

    private static long? TraverseDependencyBytes(VersionRepresentation root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var visited = new HashSet<RepresentationId>();
        var visiting = new HashSet<RepresentationId>();
        var pending = new Stack<(VersionRepresentation Node, int NextDependency)>();
        pending.Push((root, -1));
        long total = 0;
        try
        {
            while (pending.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                var (node, next) = frame;
                if (next == -1)
                {
                    if (visited.Contains(node.RepresentationId)) continue;
                    if (!visiting.Add(node.RepresentationId)) return null;
                    var bytes = ReadBytes(node.RepresentationSpecificMetadata,
                        node.Kind == RepresentationKind.PluginArtifact ? NativeClosureBytesKey : StoredBytesKey);
                    if (bytes is null) return null;
                    // Count each object once for this root, including shared ancestors.
                    // Subtree totals cannot be summed safely across a diamond.
                    total = checked(total + bytes.Value);
                    next = 0;
                }
                if (next == node.DependencyRepresentationIds.Length)
                {
                    visiting.Remove(node.RepresentationId);
                    visited.Add(node.RepresentationId);
                    continue;
                }
                var found = graph.TryGetValue(node.DependencyRepresentationIds[next], out var dependency);
                token.ThrowIfCancellationRequested();
                if (!found) return null;
                pending.Push((node, next + 1));
                pending.Push((dependency!, -1));
            }
            token.ThrowIfCancellationRequested();
            return total;
        }
        catch (OverflowException) { token.ThrowIfCancellationRequested(); return null; }
    }
}
