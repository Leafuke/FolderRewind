using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FolderRewind.History.Domain;

public enum HistoryOrigin
{
    Native = 0,
    LegacyMigration = 1,
    LegacyMetadataRecovery = 2,
    LegacyDeclaration = 3,
    Recovery = 4,
    CloudImport = 5,
    UserImport = 6
}

public sealed record HistoryProvenance(
    HistoryOrigin Origin,
    string DeviceId,
    string Detail)
{
    public static HistoryProvenance Native(string deviceId)
        => new(HistoryOrigin.Native, deviceId?.Trim() ?? string.Empty, string.Empty);
}

public enum HistoryDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record HistoryDiagnostic(
    string Code,
    HistoryDiagnosticSeverity Severity,
    string Message);

internal static class DomainCollections
{
    public static ImmutableArray<T> Freeze<T>(IEnumerable<T>? values)
        => values is null ? ImmutableArray<T>.Empty : [.. values];

    public static ImmutableSortedDictionary<string, string> FreezeMetadata(
        IEnumerable<KeyValuePair<string, string>>? values)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var pair in values)
            {
                builder[pair.Key] = pair.Value;
            }
        }

        return builder.ToImmutable();
    }
}
