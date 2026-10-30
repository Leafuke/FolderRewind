using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services
{
    public static class FolderNameConflictService
    {
        public sealed class IntraConfigConflict
        {
            public string ConfigName { get; init; } = string.Empty;
            public IReadOnlyList<string> FolderDisplayNames { get; init; } = Array.Empty<string>();
        }

        public sealed class SharedDestinationConflict
        {
            public string DestinationPath { get; init; } = string.Empty;
            public IReadOnlyList<string> ConfigNames { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> FolderDisplayNames { get; init; } = Array.Empty<string>();
        }

        public static string ResolveDisplayName(ManagedFolder? folder)
        {
            if (folder == null)
            {
                return string.Empty;
            }

            return ResolveDisplayName(folder.DisplayName, folder.Path);
        }

        public static string ResolveDisplayName(string? displayName, string? folderPath = null)
        {
            var trimmedDisplayName = displayName?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedDisplayName))
            {
                return trimmedDisplayName;
            }

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            var normalizedPath = folderPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(normalizedPath);
        }

        public static IReadOnlyList<string> GetDuplicateDisplayNames(BackupConfig? config)
        {
            if (config?.SourceFolders == null)
            {
                return Array.Empty<string>();
            }

            return config.SourceFolders
                .Select(ResolveDisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.First())
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        public static IReadOnlyList<IntraConfigConflict> FindIntraConfigConflicts(AppConfig? appConfig)
        {
            if (appConfig?.BackupConfigs == null)
            {
                return Array.Empty<IntraConfigConflict>();
            }

            return appConfig.BackupConfigs
                .Select(config => new IntraConfigConflict
                {
                    ConfigName = ResolveConfigName(config),
                    FolderDisplayNames = GetDuplicateDisplayNames(config)
                })
                .Where(conflict => conflict.FolderDisplayNames.Count > 0)
                .ToArray();
        }

        public static IReadOnlyList<SharedDestinationConflict> FindSharedDestinationConflicts(AppConfig? appConfig)
        {
            if (appConfig?.BackupConfigs == null)
            {
                return Array.Empty<SharedDestinationConflict>();
            }

            return appConfig.BackupConfigs
                .GroupBy(config => NormalizeDestinationPath(config.DestinationPath), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                    && group.Select(config => config.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(BuildSharedDestinationConflict)
                .Where(conflict => conflict.FolderDisplayNames.Count > 0)
                .ToArray();
        }

        public static string NormalizeDestinationPath(string? destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                return string.Empty;
            }

            var trimmedPath = destinationPath.Trim();

            try
            {
                return Path.GetFullPath(trimmedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return trimmedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static SharedDestinationConflict BuildSharedDestinationConflict(IGrouping<string, BackupConfig> group)
        {
            var folderEntries = group
                .SelectMany(config => (config.SourceFolders ?? Enumerable.Empty<ManagedFolder>())
                    .Select(folder => new
                    {
                        ConfigId = config.Id,
                        ConfigName = ResolveConfigName(config),
                        FolderName = ResolveDisplayName(folder)
                    }))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.FolderName))
                .ToList();

            var conflictingFolderGroups = folderEntries
                .GroupBy(entry => entry.FolderName, StringComparer.OrdinalIgnoreCase)
                .Where(folderGroup => folderGroup.Select(entry => entry.ConfigId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToList();

            var configNames = conflictingFolderGroups
                .SelectMany(folderGroup => folderGroup.Select(entry => entry.ConfigName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var folderDisplayNames = conflictingFolderGroups
                .Select(folderGroup => folderGroup.First().FolderName)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            return new SharedDestinationConflict
            {
                DestinationPath = group.Key,
                ConfigNames = configNames,
                FolderDisplayNames = folderDisplayNames
            };
        }

        private static string ResolveConfigName(BackupConfig? config)
        {
            var configName = config?.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(configName))
            {
                return configName;
            }

            if (!string.IsNullOrWhiteSpace(config?.Id))
            {
                return config.Id;
            }

            return I18n.GetString("Config_DefaultBackupName");
        }
    }
}