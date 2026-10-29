using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.History.Migration;

/// <summary>Released 1.8.2 metadata is evidence of content, never of deletion authority.</summary>
public sealed record LegacySmartPlan(string[] Chain, IReadOnlyDictionary<string, string> Owners)
{
    public const string ContractKey = "legacy182Contract";
    public const string OwnersKey = "legacy182Owners";
    public const string FilesKey = "legacy182Files";

    public static LegacySmartRecordSnapshot[] RecordsForTarget(SourceId source, string? archivePath,
        IEnumerable<LegacySmartRecordSnapshot> records)
    {
        var directory = string.IsNullOrEmpty(archivePath) ? null : Path.GetDirectoryName(archivePath);
        return records.Where(r => r.SourceId == source).GroupBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var local = directory is null ? [] : group.Where(r => !string.IsNullOrEmpty(r.ArchivePath)
                    && StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(r.ArchivePath), directory)).ToArray();
                return local.Length > 0 ? local : group.ToArray();
            }).ToArray();
    }

    public static LegacySmartPlan Build(SourceId source, string target, IEnumerable<LegacySmartRecordSnapshot> records)
    {
        var map = records.Where(r => r.SourceId == source)
            .GroupBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        var owners = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reverse = new List<LegacySmartRecordSnapshot>();
        var cursor = target;
        while (true)
        {
            if (!SafeArchive(cursor) || !visiting.Add(cursor))
                throw new InvalidDataException("Unsafe or cyclic legacy dependency chain.");
            if (!map.TryGetValue(cursor, out var candidates))
                throw new InvalidDataException($"Legacy metadata missing: {cursor}");
            if (candidates.Length != 1)
                throw new InvalidDataException($"Conflicting legacy metadata: {cursor}");
            var record = candidates[0];
            if (record.Diagnostic.Length != 0) throw new InvalidDataException(record.Diagnostic);
            reverse.Add(record);
            if (record.BackupType.Equals("Full", StringComparison.OrdinalIgnoreCase)) break;
            if (!record.BackupType.Equals("Smart", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Legacy dependency is not a proven Full or Smart record.");
            cursor = record.PreviousBackupFileName;
        }
        reverse.Reverse();
        string? baseFull = null;
        foreach (var record in reverse)
        {
            var name = record.ArchiveFileName;
            var expected = Paths(record.FullFileList);
            if (record.BackupType.Equals("Full", StringComparison.OrdinalIgnoreCase))
            {
                baseFull = name;
                foreach (var file in expected) owners.Add(file, name);
            }
            else if (record.BackupType.Equals("Smart", StringComparison.OrdinalIgnoreCase))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(baseFull, record.BasedOnFullBackup))
                    throw new InvalidDataException("Legacy Smart base does not match its predecessor chain.");
                foreach (var file in Paths(record.DeletedFiles))
                    if (!owners.Remove(file)) throw new InvalidDataException("Legacy deletion references an absent file.");
                foreach (var file in Paths(record.AddedFiles))
                    if (!owners.TryAdd(file, name)) throw new InvalidDataException("Legacy addition references an existing file.");
                foreach (var file in Paths(record.ModifiedFiles))
                {
                    if (!owners.ContainsKey(file)) throw new InvalidDataException("Legacy modification references an absent file.");
                    owners[file] = name;
                }
            }
            else throw new InvalidDataException("Legacy dependency is not a proven Full or Smart record.");
            if (!expected.SetEquals(owners.Keys)) throw new InvalidDataException("Legacy target manifest disagrees with its change chain.");
            foreach (var file in owners.Keys)
            {
                var parent = file;
                while (parent.Contains('/'))
                {
                    parent = parent[..parent.LastIndexOf('/')];
                    if (owners.ContainsKey(parent)) throw new InvalidDataException("Legacy manifest has a file/directory collision.");
                }
            }
            chain.Add(name);
        }
        return new(chain.ToArray(), owners);
    }

    public static HashSet<string> Paths(IEnumerable<string>? paths)
    {
        if (paths is null) throw new InvalidDataException("Required legacy manifest field is absent.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalized = (path ?? "").Replace('\\', '/');
            if (!HistoryRepositoryPaths.IsSafeRepositoryRelativePath(normalized)
                || normalized.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || normalized.Split('/').Any(p => p.EndsWith('.') || p.EndsWith(' '))
                || !result.Add(normalized))
                throw new InvalidDataException("Unsafe or duplicate legacy manifest path.");
        }
        return result;
    }

    public static bool SafeArchive(string name) => !string.IsNullOrWhiteSpace(name)
        && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
        && name is not "." and not ".." && !name.EndsWith('.') && !name.EndsWith(' ');

    public string EncodeOwners() => JsonSerializer.Serialize(Owners);
}
