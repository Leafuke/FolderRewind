using FolderRewind.History.Domain;
using FolderRewind.History.Migration;

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
    public static IReadOnlyList<LegacySmartRecordSnapshot> Read(IEnumerable<(SourceId SourceId, string ArchiveDirectory)> locations)
    {
        var all = new List<LegacySmartRecordSnapshot>();
        foreach (var location in locations.Distinct())
        {
            var selected = new Dictionary<string, (int Priority, LegacySmartRecordSnapshot Record)>(StringComparer.OrdinalIgnoreCase);
            var metadata = Path.Combine(location.ArchiveDirectory, "_metadata");
            void ReadFile(string path, int priority, bool aggregate)
            {
                if (!File.Exists(path)) return;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                    var elements = aggregate
                        ? Property(document.RootElement, "backupRecords")?.EnumerateArray().ToArray() ?? []
                        : new[] { document.RootElement };
                    foreach (var element in elements)
                    {
                        var name = Text(element, "archiveFileName");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var record = new LegacySmartRecordSnapshot(location.SourceId, name,
                            Text(element, "previousBackupFileName"), Text(element, "basedOnFullBackup"), Text(element, "backupType"),
                            Array(element, "addedFiles"), Array(element, "modifiedFiles"), Array(element, "deletedFiles"), Array(element, "fullFileList"),
                            ArchivePath: LegacySmartPlan.SafeArchive(name) ? Path.Combine(location.ArchiveDirectory, name) : "");
                        if (!selected.TryGetValue(name, out var previous) || priority > previous.Priority)
                            selected[name] = (priority, record);
                        else if (priority == previous.Priority && JsonSerializer.Serialize(record) != JsonSerializer.Serialize(previous.Record))
                            selected[name] = (priority, previous.Record with { Diagnostic = "Conflicting legacy metadata for " + name });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    // An unreadable preferred record must not silently fall back to stale aggregate metadata.
                    throw new InvalidDataException("Cannot read legacy metadata: " + path, ex);
                }
            }
            ReadFile(Path.Combine(metadata, "metadata.legacy.json"), 0, true);
            ReadFile(Path.Combine(metadata, "metadata.json"), 1, true);
            var records = Path.Combine(metadata, "records");
            if (Directory.Exists(records))
                foreach (var path in Directory.EnumerateFiles(records, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    ReadFile(path, 2, false);
            all.AddRange(selected.Values.Select(v => v.Record));
        }
        return all;
    }
    private static JsonElement? Property(JsonElement element, string name)
        => element.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => (JsonElement?)p.Value).FirstOrDefault();
    private static string Text(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static string[]? Array(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : null;
}
