using System;

namespace FolderRewind.Services
{
    internal static class BackupArchiveTypePolicy
    {
        public static string InferFromFileName(string? backupFileName)
        {
            if (string.IsNullOrWhiteSpace(backupFileName))
            {
                return "Full";
            }

            if (backupFileName.Contains("[Smart]", StringComparison.OrdinalIgnoreCase))
            {
                return "Smart";
            }

            if (backupFileName.Contains("[Rolling]", StringComparison.OrdinalIgnoreCase))
            {
                return "Rolling";
            }

            if (backupFileName.Contains("[Overwrite]", StringComparison.OrdinalIgnoreCase))
            {
                return "Overwrite";
            }

            return "Full";
        }

        public static bool IsIncremental(string? backupType)
        {
            return !string.IsNullOrWhiteSpace(backupType)
                && (backupType.Equals("Incremental", StringComparison.OrdinalIgnoreCase)
                    || backupType.Equals("Smart", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// A self-contained archive can restore its snapshot without another archive.
        /// Overwrite is accepted only as a legacy on-disk type; new writes use Rolling.
        /// </summary>
        public static bool IsSelfContained(string? backupType, string? archiveFileName = null)
        {
            string effectiveType = string.IsNullOrWhiteSpace(backupType)
                ? InferFromFileName(archiveFileName)
                : backupType;
            return effectiveType.Equals("Full", StringComparison.OrdinalIgnoreCase)
                || effectiveType.Equals("Rolling", StringComparison.OrdinalIgnoreCase)
                || effectiveType.Equals("Overwrite", StringComparison.OrdinalIgnoreCase);
        }
    }
}
