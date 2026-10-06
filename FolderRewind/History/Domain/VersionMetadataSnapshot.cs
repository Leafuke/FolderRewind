using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public sealed record VersionMetadataSnapshot
{
    private const string IdentityDomain = "folderrewind/version-metadata-snapshot/v1";
    public const int MaximumPayloadUtf8Bytes = 64 * 1024;

    [JsonConstructor]
    public VersionMetadataSnapshot(
        MetadataSnapshotId metadataSnapshotId,
        VersionId versionId,
        string producerPluginId,
        string schemaId,
        int schemaVersion,
        JsonElement payload,
        DateTimeOffset capturedAtUtc)
    {
        ProducerPluginId = RequireIdentity(producerPluginId, nameof(producerPluginId));
        SchemaId = RequireIdentity(schemaId, nameof(schemaId));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (payload.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Version metadata payload cannot be undefined.", nameof(payload));
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaximumPayloadUtf8Bytes)
            throw new ArgumentException("Version metadata payload exceeds its quota.", nameof(payload));

        var expectedId = CreateId(versionId, ProducerPluginId, SchemaId, schemaVersion);
        if (metadataSnapshotId != expectedId)
            throw new ArgumentException("MetadataSnapshotId does not match its semantic identity.", nameof(metadataSnapshotId));
        MetadataSnapshotId = metadataSnapshotId;
        VersionId = versionId;
        SchemaVersion = schemaVersion;
        Payload = payload.Clone();
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
    }

    public MetadataSnapshotId MetadataSnapshotId { get; }
    public VersionId VersionId { get; }
    public string ProducerPluginId { get; }
    public string SchemaId { get; }
    public int SchemaVersion { get; }
    public JsonElement Payload { get; }
    public DateTimeOffset CapturedAtUtc { get; }

    public static VersionMetadataSnapshot Create(
        VersionId versionId,
        string producerPluginId,
        string schemaId,
        int schemaVersion,
        JsonElement payload,
        DateTimeOffset capturedAtUtc)
        => new(
            CreateId(versionId, producerPluginId, schemaId, schemaVersion),
            versionId,
            producerPluginId,
            schemaId,
            schemaVersion,
            payload,
            capturedAtUtc);

    public static MetadataSnapshotId CreateId(
        VersionId versionId,
        string producerPluginId,
        string schemaId,
        int schemaVersion)
    {
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        return MetadataSnapshotId.FromGuid(DeterministicHistoryId.Create(
            IdentityDomain,
            [
                versionId.ToString(),
                RequireIdentity(producerPluginId, nameof(producerPluginId)),
                RequireIdentity(schemaId, nameof(schemaId)),
                schemaVersion.ToString(CultureInfo.InvariantCulture)
            ]));
    }

    private static string RequireIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Metadata identity cannot be empty.", parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 128
            || !IsAsciiLowerOrDigit(normalized[0])
            || normalized.Any(character => !IsAsciiLowerOrDigit(character)
                && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Metadata identity is not canonical.", parameterName);
        }
        return normalized;

        static bool IsAsciiLowerOrDigit(char character)
            => character is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}
