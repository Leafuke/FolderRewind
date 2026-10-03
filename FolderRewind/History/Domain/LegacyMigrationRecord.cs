using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum LegacyMigrationVisibility
{
    Timeline = 0,
    SupportOnly = 1
}

public sealed record LegacyMigrationRecord
{
    public const string MigrationContract = "LegacyMigrationV1";

    public LegacyMigrationRecord(
        LegacyMigrationRecordId recordId,
        string legacyOriginKey,
        VersionId versionId,
        LegacyMigrationVisibility visibility,
        DateTimeOffset legacyTimestampUtc,
        IEnumerable<HistoryDiagnostic>? diagnostics)
        : this(
            recordId,
            MigrationContract,
            legacyOriginKey,
            versionId,
            visibility,
            legacyTimestampUtc,
            diagnostics is null ? [] : [.. diagnostics])
    {
    }

    [JsonConstructor]
    public LegacyMigrationRecord(
        LegacyMigrationRecordId recordId,
        string contract,
        string legacyOriginKey,
        VersionId versionId,
        LegacyMigrationVisibility visibility,
        DateTimeOffset legacyTimestampUtc,
        ImmutableArray<HistoryDiagnostic> diagnostics)
    {
        if (!StringComparer.Ordinal.Equals(contract, MigrationContract))
            throw new ArgumentException("Unsupported Legacy migration contract.", nameof(contract));
        RecordId = recordId;
        Contract = contract;
        LegacyOriginKey = string.IsNullOrWhiteSpace(legacyOriginKey)
            ? throw new ArgumentException("Legacy origin key is required.", nameof(legacyOriginKey))
            : legacyOriginKey;
        VersionId = versionId;
        Visibility = visibility;
        LegacyTimestampUtc = legacyTimestampUtc.ToUniversalTime();
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    public LegacyMigrationRecordId RecordId { get; }
    public string Contract { get; }
    public string LegacyOriginKey { get; }
    public VersionId VersionId { get; }
    public LegacyMigrationVisibility Visibility { get; }
    public DateTimeOffset LegacyTimestampUtc { get; }
    public ImmutableArray<HistoryDiagnostic> Diagnostics { get; }
}
