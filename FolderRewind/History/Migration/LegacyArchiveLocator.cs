using FolderRewind.History.Domain;
using FolderRewind.History.Legacy;
using FolderRewind.Plugin.Runtime.Configuration;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Migration;

public sealed record LegacyArchiveLocation(string[] Candidates, string? Selected, string Diagnostic);

public static class LegacyArchiveLocator
{
    public static LegacyMigrationSourceSnapshot? ResolveSource(LegacyHistoryRecord record, IEnumerable<LegacyMigrationSourceSnapshot> sources)
    {
        var roster = sources.ToArray();
        var paths = roster.Where(s => LegacySourceIdentityV1.NormalizePath(s.OriginalPath)
            == LegacySourceIdentityV1.NormalizePath(record.FolderPath)).ToArray();
        if (record.FolderId is { } id)
        {
            var match = roster.SingleOrDefault(s => s.SourceId.Value == id);
            return match is not null && (paths.Length == 0 || paths.Length == 1 && paths[0].SourceId == match.SourceId) ? match : null;
        }
        return paths.Length == 1 ? paths[0] : null;
    }

    public static LegacyArchiveLocation Locate(string destination, LegacyHistoryRecord record,
        LegacyMigrationSourceSnapshot? source, string? selected = null)
    {
        if (!LegacySmartPlan.SafeArchive(record.FileName)) return new([], null, "Unsafe archive filename.");
        var candidates = new List<string>();
        foreach (var name in new[] { record.FolderName, source?.DisplayName })
            if (BackupStoragePathService.TryResolveBackupStoragePaths(destination, name ?? "", record.FolderPath,
                out _, out var directory, out _)) candidates.Add(Path.Combine(directory, record.FileName));
        if (!string.IsNullOrWhiteSpace(selected))
        {
            string path;
            try
            {
                if (!Path.IsPathFullyQualified(selected)) return new(candidates.ToArray(), null, "Select an absolute archive path.");
                path = Path.GetFullPath(selected);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { return new(candidates.ToArray(), null, "Invalid selected archive path: " + ex.Message); }
            candidates.Insert(0, path);
            return new(candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                File.Exists(path) ? path : null, File.Exists(path) ? "" : "Selected archive is offline or missing.");
        }
        var unique = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var available = unique.Where(File.Exists).ToArray();
        return new(unique, available.Length == 1 ? available[0] : null,
            available.Length > 1 ? "Multiple archive candidates require an explicit selection." : available.Length == 0 ? "Archive is offline or missing." : "");
    }
}
