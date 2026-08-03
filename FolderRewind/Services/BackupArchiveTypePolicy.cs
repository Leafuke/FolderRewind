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
    }
}
