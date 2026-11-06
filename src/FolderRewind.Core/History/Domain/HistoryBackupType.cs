using System.Text.Json;

namespace FolderRewind.History.Domain;

public enum HistoryBackupType { Unknown, Full, Smart, Rolling, SemanticFull, SemanticSmart, Semantic }

public static class HistoryBackupTypePolicy
{
    public const string MetadataKey = "hostBackupType";

    public static HistoryBackupType Resolve(VersionMetadataSnapshot? capture, VersionRepresentation? representation)
    {
        if (capture is not null && BackupSizeMetadata.IsCaptureStatistics(capture.ProducerPluginId, capture.SchemaId)
            && capture.SchemaVersion == BackupSizeMetadata.SchemaVersion && capture.Payload.ValueKind == JsonValueKind.Object
            && capture.Payload.TryGetProperty("backupType", out var property) && property.ValueKind == JsonValueKind.String
            && Parse(property.GetString()) is var captured && captured != HistoryBackupType.Unknown)
            return captured;
        if (representation is null) return HistoryBackupType.Unknown;
        if (representation.RepresentationSpecificMetadata.TryGetValue(MetadataKey, out var text)
            && Parse(text) is var stored && stored != HistoryBackupType.Unknown) return stored;
        if (representation.Kind == RepresentationKind.LegacyArchive
            && representation.RepresentationSpecificMetadata.TryGetValue("legacyBackupType", out var legacy)) return Parse(legacy);
        return FromKind(representation.Kind);
    }

    public static HistoryBackupType FromKind(RepresentationKind kind) => kind switch
    {
        RepresentationKind.CoreFull => HistoryBackupType.Full,
        RepresentationKind.CoreSmartDelta => HistoryBackupType.Smart,
        RepresentationKind.CoreRolling => HistoryBackupType.Rolling,
        RepresentationKind.PluginArtifact => HistoryBackupType.Semantic,
        _ => HistoryBackupType.Unknown
    };

    private static HistoryBackupType Parse(string? value) => value switch
    {
        "Full" => HistoryBackupType.Full,
        "Smart" => HistoryBackupType.Smart,
        "Rolling" => HistoryBackupType.Rolling,
        "SemanticFull" => HistoryBackupType.SemanticFull,
        "SemanticSmart" => HistoryBackupType.SemanticSmart,
        "Semantic" => HistoryBackupType.Semantic,
        _ => HistoryBackupType.Unknown
    };
}
