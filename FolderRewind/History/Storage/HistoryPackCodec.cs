using FolderRewind.History.Domain;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FolderRewind.History.Storage;

public sealed class HistoryPackCodec
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public HistoryPackObject CreateObject(object value, int schemaVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (kind, id) = GetIdentity(value);
        var element = JsonSerializer.SerializeToElement(value, value.GetType(), PayloadOptions);
        var payload = Canonicalize(element);
        return new HistoryPackObject(kind, schemaVersion, id, ComputeSha256(payload), payload);
    }

    public HistoryPackObject CreateUnknownObject(
        string kind,
        int schemaVersion,
        string id,
        JsonElement payload)
    {
        var canonical = Canonicalize(payload);
        return new HistoryPackObject(kind, schemaVersion, id, ComputeSha256(canonical), canonical);
    }

    public byte[] Encode(HistoryCommitPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", pack.FormatVersion);
            writer.WriteString("packId", pack.PackId.ToString());
            writer.WriteString("transactionId", pack.TransactionId.ToString());
            writer.WriteString("createdAtUtc", pack.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var item in pack.Objects)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Kind);
                writer.WriteNumber("schemaVersion", item.SchemaVersion);
                writer.WriteString("id", item.Id);
                writer.WriteString("payloadHash", item.PayloadHash);
                writer.WritePropertyName("payload");
                writer.WriteRawValue(item.CanonicalPayload, skipInputValidation: false);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public HistoryPackReadResult Decode(ReadOnlySpan<byte> originalBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(originalBytes.ToArray());
            var root = document.RootElement;
            var formatVersion = root.GetProperty("formatVersion").GetInt32();
            if (formatVersion != HistoryCommitPack.CurrentFormatVersion)
            {
                throw new HistoryPackCompatibilityException($"Unsupported Commit Pack format {formatVersion}.");
            }

            var packId = PackId.Parse(root.GetProperty("packId").GetString() ?? string.Empty);
            var transactionId = HistoryTransactionId.Parse(root.GetProperty("transactionId").GetString() ?? string.Empty);
            var createdAtUtc = root.GetProperty("createdAtUtc").GetDateTimeOffset().ToUniversalTime();
            var objects = new List<HistoryPackObject>();
            var unsupported = false;
            foreach (var objectElement in root.GetProperty("objects").EnumerateArray())
            {
                var kind = objectElement.GetProperty("kind").GetString() ?? string.Empty;
                var schemaVersion = objectElement.GetProperty("schemaVersion").GetInt32();
                var id = objectElement.GetProperty("id").GetString() ?? string.Empty;
                var expectedHash = objectElement.GetProperty("payloadHash").GetString() ?? string.Empty;
                var payload = Canonicalize(objectElement.GetProperty("payload"));
                var actualHash = ComputeSha256(payload);
                if (!StringComparer.Ordinal.Equals(expectedHash, actualHash))
                {
                    throw new HistoryRepositoryValidationException(
                        $"Payload hash mismatch for {kind}/{id}.");
                }

                var item = new HistoryPackObject(kind, schemaVersion, id, actualHash, payload);
                if (!HistoryObjectKinds.IsKnown(kind) || schemaVersion != 1)
                {
                    unsupported = true;
                }
                else
                {
                    var domainObject = DeserializeKnown(item);
                    var identity = GetIdentity(domainObject);
                    if (!StringComparer.Ordinal.Equals(identity.Kind, kind)
                        || !StringComparer.Ordinal.Equals(identity.Id, id))
                    {
                        throw new HistoryRepositoryValidationException(
                            $"Envelope identity does not match payload identity for {kind}/{id}.");
                    }
                }

                objects.Add(item);
            }

            var pack = new HistoryCommitPack(packId, transactionId, createdAtUtc, objects);
            return new HistoryPackReadResult(pack, originalBytes.ToArray(), unsupported);
        }
        catch (HistoryRepositoryException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new HistoryRepositoryValidationException($"Invalid Commit Pack: {ex.Message}");
        }
    }

    public object DeserializeKnown(HistoryPackObject item)
    {
        if (item.SchemaVersion != 1)
        {
            throw new HistoryPackCompatibilityException(
                $"Unsupported schema {item.SchemaVersion} for {item.Kind}.");
        }

        var type = item.Kind switch
        {
            HistoryObjectKinds.SourceVersion => typeof(SourceVersion),
            HistoryObjectKinds.ConfigurationCheckpoint => typeof(ConfigurationCheckpoint),
            HistoryObjectKinds.VersionRepresentation => typeof(VersionRepresentation),
            HistoryObjectKinds.StorageReplica => typeof(StorageReplica),
            HistoryObjectKinds.ReplicaLifecycleUpdate => typeof(ReplicaLifecycleUpdate),
            HistoryObjectKinds.BranchUpdate => typeof(BranchUpdate),
            HistoryObjectKinds.BackupRun => typeof(BackupRun),
            HistoryObjectKinds.HistoryAnnotationUpdate => typeof(HistoryAnnotationUpdate),
            HistoryObjectKinds.MaterializationPolicyUpdate => typeof(MaterializationPolicyUpdate),
            HistoryObjectKinds.LegacyMigrationRecord => typeof(LegacyMigrationRecord),
            HistoryObjectKinds.SafetySnapshot => typeof(SafetySnapshot),
            HistoryObjectKinds.SafetySnapshotRelease => typeof(SafetySnapshotRelease),
            HistoryObjectKinds.VersionMetadataSnapshot => typeof(VersionMetadataSnapshot),
            _ => throw new HistoryPackCompatibilityException($"Unknown object kind '{item.Kind}'.")
        };

        return JsonSerializer.Deserialize(item.CanonicalPayload, type, PayloadOptions)
            ?? throw new HistoryRepositoryValidationException($"Null payload for {item.Kind}/{item.Id}.");
    }

    public static byte[] Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }

        return stream.ToArray();
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new HistoryRepositoryValidationException("Undefined JSON values are not canonical.");
        }
    }

    private static (string Kind, string Id) GetIdentity(object value)
        => value switch
        {
            SourceVersion item => (HistoryObjectKinds.SourceVersion, item.VersionId.ToString()),
            ConfigurationCheckpoint item => (HistoryObjectKinds.ConfigurationCheckpoint, item.CheckpointId.ToString()),
            VersionRepresentation item => (HistoryObjectKinds.VersionRepresentation, item.RepresentationId.ToString()),
            StorageReplica item => (HistoryObjectKinds.StorageReplica, item.ReplicaId.ToString()),
            ReplicaLifecycleUpdate item => (HistoryObjectKinds.ReplicaLifecycleUpdate, item.UpdateId.ToString()),
            BranchUpdate item => (HistoryObjectKinds.BranchUpdate, item.UpdateId.ToString()),
            BackupRun item => (HistoryObjectKinds.BackupRun, item.RunId.ToString()),
            HistoryAnnotationUpdate item => (HistoryObjectKinds.HistoryAnnotationUpdate, item.UpdateId.ToString()),
            MaterializationPolicyUpdate item => (HistoryObjectKinds.MaterializationPolicyUpdate, item.UpdateId.ToString()),
            LegacyMigrationRecord item => (HistoryObjectKinds.LegacyMigrationRecord, item.RecordId.ToString()),
            SafetySnapshot item => (HistoryObjectKinds.SafetySnapshot, item.SnapshotId.ToString()),
            SafetySnapshotRelease item => (HistoryObjectKinds.SafetySnapshotRelease, item.ReleaseId.ToString()),
            VersionMetadataSnapshot item => (HistoryObjectKinds.VersionMetadataSnapshot, item.MetadataSnapshotId.ToString()),
            _ => throw new ArgumentException($"Unsupported history object type {value.GetType().FullName}.", nameof(value))
        };
}
