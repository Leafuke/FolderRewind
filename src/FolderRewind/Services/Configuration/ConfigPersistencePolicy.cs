using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

// Shared by snapshot capture and edits that compare their applied values during compensation.
internal static class ConfigPersistencePolicy
{
    internal static ObservableCollection<string> NormalizeStrings(ObservableCollection<string>? values, bool paths)
    {
        var normalized = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => paths ? value.Trim().Replace('\\', '/') : value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values is not null && values.SequenceEqual(normalized) ? values : new(normalized);
    }

    internal static void NormalizeSourceScopeForPersistence(BackupSourceScope scope)
        => scope.IncludePatterns = NormalizeStrings(scope.IncludePatterns, paths: true);

    internal static void PrepareSourceIdentities(AppConfig config, bool repair)
    {
        var used = new HashSet<Guid>();
        foreach (var project in config.BackupConfigs.Where(item => item is not null))
        {
            foreach (var folder in project.SourceFolders.Where(item => item is not null))
            {
                var valid = Guid.TryParse(folder.Id, out var id) && id != Guid.Empty;
                var duplicate = valid && !used.Add(id);
                if (valid && !duplicate) continue;
                if (!repair) throw new ConfigSourceIdentityException(duplicate);
                do { id = Guid.NewGuid(); } while (!used.Add(id));
                folder.Id = id.ToString();
            }
        }
    }
}

internal sealed class ConfigSourceIdentityException(bool duplicate)
    : IOException(duplicate ? "configuration.source-id-duplicate" : "configuration.source-id-invalid")
{
    internal bool Duplicate { get; } = duplicate;
}
