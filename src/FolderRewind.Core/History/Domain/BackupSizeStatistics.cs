using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

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
    {
        if (root is null) return null;
        var visited = new HashSet<RepresentationId>();
        var visiting = new HashSet<RepresentationId>();
        long total = 0;
        try { return Visit(root) ? total : null; }
        catch (OverflowException) { return null; }

        bool Visit(VersionRepresentation node)
        {
            if (visited.Contains(node.RepresentationId)) return true;
            if (!visiting.Add(node.RepresentationId)) return false;
            var bytes = ReadBytes(node.RepresentationSpecificMetadata,
                node.Kind == RepresentationKind.PluginArtifact ? NativeClosureBytesKey : StoredBytesKey);
            if (bytes is null) return false;
            total = checked(total + bytes.Value);
            foreach (var id in node.DependencyRepresentationIds)
                if (!graph.TryGetValue(id, out var dependency) || !Visit(dependency)) return false;
            visiting.Remove(node.RepresentationId);
            visited.Add(node.RepresentationId);
            return true;
        }
    }
}
