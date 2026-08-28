using FolderRewind.History.Domain;
using FolderRewind.History.Migration;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FolderRewind.History.Legacy;

public sealed record LegacyHistoryRecord
{
    public string ConfigId { get; init; } = string.Empty;
    public Guid? FolderId { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public string FolderName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string BackupType { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public bool IsImportant { get; init; }
    public bool IsPartialBackup { get; init; }
    public bool IsCloudArchived { get; init; }
    public string CloudArchiveRemotePath { get; init; } = string.Empty;
}

public static class LegacyHistoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<LegacyHistoryRecord> Read(string historyPath)
    {
        if (!File.Exists(historyPath)) return [];
        var bytes = File.ReadAllBytes(historyPath);
        return JsonSerializer.Deserialize<List<LegacyHistoryRecord>>(bytes, JsonOptions) ?? [];
    }
}

public sealed record LegacyArchiveName(
    string BackupType,
    DateTime Timestamp,
    string SourceDisplayName,
    string Comment,
    string Format);

public static partial class LegacyArchiveNameParser
{
    [GeneratedRegex(
        @"^\[(Full|Smart|Rolling|Overwrite)\]\[(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\](.+?)(?:\s\[(.+?)\])?\.(7z|zip)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string fileName, out LegacyArchiveName? result)
    {
        result = null;
        var match = Pattern().Match(Path.GetFileName(fileName));
        if (!match.Success
            || !DateTime.TryParseExact(
                match.Groups[2].Value,
                "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
            return false;
        result = new(
            match.Groups[1].Value,
            timestamp,
            match.Groups[3].Value.Trim(),
            match.Groups[4].Success ? match.Groups[4].Value : string.Empty,
            match.Groups[5].Value.ToLowerInvariant());
        return true;
    }
}

public static class LegacySmartMetadataReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<LegacySmartRecordSnapshot> Read(BackupConfig config)
    {
        var result = new List<LegacySmartRecordSnapshot>();
        foreach (var folder in config.SourceFolders)
        {
            if (!Guid.TryParse(folder.Id, out var sourceGuid) || sourceGuid == Guid.Empty
                || !BackupStoragePathService.TryResolveBackupStoragePaths(
                    config.DestinationPath,
                    folder.DisplayName,
                    folder.Path,
                    out _,
                    out _,
                    out var metadataDirectory))
                continue;
            var sourceId = new SourceId(sourceGuid);
            ReadRecordsDirectory(metadataDirectory, sourceId, result);
            ReadAggregate(Path.Combine(metadataDirectory, "metadata.json"), sourceId, result);
            ReadAggregate(Path.Combine(metadataDirectory, "metadata.legacy.json"), sourceId, result);
        }
        return result
            .DistinctBy(item => (item.SourceId, item.ArchiveFileName), LegacySmartRecordKeyComparer.Instance)
            .ToArray();
    }

    private static void ReadRecordsDirectory(
        string metadataDirectory,
        SourceId sourceId,
        ICollection<LegacySmartRecordSnapshot> output)
    {
        var records = Path.Combine(metadataDirectory, "records");
        if (!Directory.Exists(records)) return;
        foreach (var path in Directory.EnumerateFiles(records, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                Add(document.RootElement, sourceId, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Migration remains fail-safe: an unreadable optional Smart record is treated as absent.
            }
        }
    }

    private static void ReadAggregate(
        string path,
        SourceId sourceId,
        ICollection<LegacySmartRecordSnapshot> output)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("backupRecords", out var records)
                || records.ValueKind != JsonValueKind.Array)
                return;
            foreach (var record in records.EnumerateArray()) Add(record, sourceId, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static void Add(
        JsonElement element,
        SourceId sourceId,
        ICollection<LegacySmartRecordSnapshot> output)
    {
        var archive = String(element, "archiveFileName");
        if (string.IsNullOrWhiteSpace(archive)) return;
        output.Add(new(
            sourceId,
            archive,
            String(element, "previousBackupFileName"),
            String(element, "basedOnFullBackup")));
    }

    private static string String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private sealed class LegacySmartRecordKeyComparer
        : IEqualityComparer<(SourceId SourceId, string ArchiveFileName)>
    {
        public static LegacySmartRecordKeyComparer Instance { get; } = new();

        public bool Equals(
            (SourceId SourceId, string ArchiveFileName) left,
            (SourceId SourceId, string ArchiveFileName) right)
            => left.SourceId == right.SourceId
               && StringComparer.OrdinalIgnoreCase.Equals(left.ArchiveFileName, right.ArchiveFileName);

        public int GetHashCode((SourceId SourceId, string ArchiveFileName) value)
            => HashCode.Combine(value.SourceId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.ArchiveFileName));
    }
}
