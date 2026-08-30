using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public interface IHistoryGuidId<TSelf>
    where TSelf : struct, IHistoryGuidId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf FromGuid(Guid value);
}

public abstract class HistoryGuidIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IHistoryGuidId<TId>
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !Guid.TryParse(reader.GetString(), out var value)
            || value == Guid.Empty)
        {
            throw new JsonException($"Invalid {typeof(TId).Name}.");
        }

        return TId.FromGuid(value);
    }

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value.ToString("N", CultureInfo.InvariantCulture));
}

internal static class HistoryGuidId
{
    public static Guid Parse(string value, string parameterName)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new FormatException($"{parameterName} must be a non-empty GUID.");
        }

        return parsed;
    }

    public static Guid Require(Guid value, string parameterName)
        => value != Guid.Empty
            ? value
            : throw new ArgumentException("Identifier cannot be empty.", parameterName);
}

[JsonConverter(typeof(HistoryConfigIdJsonConverter))]
public readonly record struct HistoryConfigId
{
    public HistoryConfigId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Config identifier cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        Value = Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty
            ? guid.ToString("N", CultureInfo.InvariantCulture)
            : trimmed.ToUpperInvariant();
    }

    public string Value { get; }

    public static HistoryConfigId Parse(string value) => new(value);

    public override string ToString() => Value;
}

public sealed class HistoryConfigIdJsonConverter : JsonConverter<HistoryConfigId>
{
    public override HistoryConfigId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? new HistoryConfigId(reader.GetString() ?? string.Empty)
            : throw new JsonException("Invalid HistoryConfigId.");

    public override void Write(Utf8JsonWriter writer, HistoryConfigId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(VersionIdJsonConverter))]
public readonly record struct VersionId : IHistoryGuidId<VersionId>
{
    public VersionId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static VersionId New() => new(Guid.NewGuid());
    public static VersionId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static VersionId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class VersionIdJsonConverter : HistoryGuidIdJsonConverter<VersionId>;

[JsonConverter(typeof(CheckpointIdJsonConverter))]
public readonly record struct CheckpointId : IHistoryGuidId<CheckpointId>
{
    public CheckpointId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static CheckpointId New() => new(Guid.NewGuid());
    public static CheckpointId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static CheckpointId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class CheckpointIdJsonConverter : HistoryGuidIdJsonConverter<CheckpointId>;

[JsonConverter(typeof(RepresentationIdJsonConverter))]
public readonly record struct RepresentationId : IHistoryGuidId<RepresentationId>
{
    public RepresentationId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static RepresentationId New() => new(Guid.NewGuid());
    public static RepresentationId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static RepresentationId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class RepresentationIdJsonConverter : HistoryGuidIdJsonConverter<RepresentationId>;

[JsonConverter(typeof(BranchIdJsonConverter))]
public readonly record struct BranchId : IHistoryGuidId<BranchId>
{
    public BranchId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static BranchId New() => new(Guid.NewGuid());
    public static BranchId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static BranchId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class BranchIdJsonConverter : HistoryGuidIdJsonConverter<BranchId>;

[JsonConverter(typeof(BranchUpdateIdJsonConverter))]
public readonly record struct BranchUpdateId : IHistoryGuidId<BranchUpdateId>
{
    public BranchUpdateId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static BranchUpdateId New() => new(Guid.NewGuid());
    public static BranchUpdateId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static BranchUpdateId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class BranchUpdateIdJsonConverter : HistoryGuidIdJsonConverter<BranchUpdateId>;

[JsonConverter(typeof(RunIdJsonConverter))]
public readonly record struct RunId : IHistoryGuidId<RunId>
{
    public RunId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static RunId New() => new(Guid.NewGuid());
    public static RunId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static RunId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class RunIdJsonConverter : HistoryGuidIdJsonConverter<RunId>;

[JsonConverter(typeof(SourceIdJsonConverter))]
public readonly record struct SourceId : IHistoryGuidId<SourceId>
{
    public SourceId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static SourceId New() => new(Guid.NewGuid());
    public static SourceId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static SourceId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class SourceIdJsonConverter : HistoryGuidIdJsonConverter<SourceId>;

[JsonConverter(typeof(ReplicaIdJsonConverter))]
public readonly record struct ReplicaId : IHistoryGuidId<ReplicaId>
{
    public ReplicaId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static ReplicaId New() => new(Guid.NewGuid());
    public static ReplicaId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static ReplicaId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class ReplicaIdJsonConverter : HistoryGuidIdJsonConverter<ReplicaId>;

[JsonConverter(typeof(LocalReplicaIdJsonConverter))]
public readonly record struct LocalReplicaId : IHistoryGuidId<LocalReplicaId>
{
    public LocalReplicaId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static LocalReplicaId New() => new(Guid.NewGuid());
    public static LocalReplicaId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static LocalReplicaId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class LocalReplicaIdJsonConverter : HistoryGuidIdJsonConverter<LocalReplicaId>;

[JsonConverter(typeof(ReplicaLifecycleUpdateIdJsonConverter))]
public readonly record struct ReplicaLifecycleUpdateId : IHistoryGuidId<ReplicaLifecycleUpdateId>
{
    public ReplicaLifecycleUpdateId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static ReplicaLifecycleUpdateId New() => new(Guid.NewGuid());
    public static ReplicaLifecycleUpdateId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static ReplicaLifecycleUpdateId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class ReplicaLifecycleUpdateIdJsonConverter : HistoryGuidIdJsonConverter<ReplicaLifecycleUpdateId>;

[JsonConverter(typeof(AnnotationUpdateIdJsonConverter))]
public readonly record struct AnnotationUpdateId : IHistoryGuidId<AnnotationUpdateId>
{
    public AnnotationUpdateId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static AnnotationUpdateId New() => new(Guid.NewGuid());
    public static AnnotationUpdateId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static AnnotationUpdateId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class AnnotationUpdateIdJsonConverter : HistoryGuidIdJsonConverter<AnnotationUpdateId>;

[JsonConverter(typeof(MaterializationPolicyUpdateIdJsonConverter))]
public readonly record struct MaterializationPolicyUpdateId : IHistoryGuidId<MaterializationPolicyUpdateId>
{
    public MaterializationPolicyUpdateId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static MaterializationPolicyUpdateId New() => new(Guid.NewGuid());
    public static MaterializationPolicyUpdateId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static MaterializationPolicyUpdateId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class MaterializationPolicyUpdateIdJsonConverter : HistoryGuidIdJsonConverter<MaterializationPolicyUpdateId>;

[JsonConverter(typeof(LegacyMigrationRecordIdJsonConverter))]
public readonly record struct LegacyMigrationRecordId : IHistoryGuidId<LegacyMigrationRecordId>
{
    public LegacyMigrationRecordId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static LegacyMigrationRecordId New() => new(Guid.NewGuid());
    public static LegacyMigrationRecordId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static LegacyMigrationRecordId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class LegacyMigrationRecordIdJsonConverter : HistoryGuidIdJsonConverter<LegacyMigrationRecordId>;

[JsonConverter(typeof(SafetySnapshotIdJsonConverter))]
public readonly record struct SafetySnapshotId : IHistoryGuidId<SafetySnapshotId>
{
    public SafetySnapshotId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static SafetySnapshotId New() => new(Guid.NewGuid());
    public static SafetySnapshotId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static SafetySnapshotId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class SafetySnapshotIdJsonConverter : HistoryGuidIdJsonConverter<SafetySnapshotId>;

[JsonConverter(typeof(SafetySnapshotReleaseIdJsonConverter))]
public readonly record struct SafetySnapshotReleaseId : IHistoryGuidId<SafetySnapshotReleaseId>
{
    public SafetySnapshotReleaseId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static SafetySnapshotReleaseId New() => new(Guid.NewGuid());
    public static SafetySnapshotReleaseId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static SafetySnapshotReleaseId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class SafetySnapshotReleaseIdJsonConverter : HistoryGuidIdJsonConverter<SafetySnapshotReleaseId>;

[JsonConverter(typeof(PackIdJsonConverter))]
public readonly record struct PackId : IHistoryGuidId<PackId>
{
    public PackId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static PackId New() => new(Guid.NewGuid());
    public static PackId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static PackId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class PackIdJsonConverter : HistoryGuidIdJsonConverter<PackId>;

[JsonConverter(typeof(HistoryTransactionIdJsonConverter))]
public readonly record struct HistoryTransactionId : IHistoryGuidId<HistoryTransactionId>
{
    public HistoryTransactionId(Guid value) => Value = HistoryGuidId.Require(value, nameof(value));
    public Guid Value { get; }
    public static HistoryTransactionId New() => new(Guid.NewGuid());
    public static HistoryTransactionId Parse(string value) => new(HistoryGuidId.Parse(value, nameof(value)));
    public static HistoryTransactionId FromGuid(Guid value) => new(HistoryGuidId.Require(value, nameof(value)));
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
public sealed class HistoryTransactionIdJsonConverter : HistoryGuidIdJsonConverter<HistoryTransactionId>;
