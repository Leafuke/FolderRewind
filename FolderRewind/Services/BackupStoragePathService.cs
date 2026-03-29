using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderRewind.Services
{
    public static class BackupStoragePathService
    {
        public static bool TryResolveStorageFolderName(string? rawFolderName, string? fallbackPath, out string storageFolderName)
        {
            var candidate = (rawFolderName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(fallbackPath))
            {
                var trimmedPath = fallbackPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                candidate = Path.GetFileName(trimmedPath);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                storageFolderName = string.Empty;
                return false;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(candidate.Length);
            foreach (var ch in candidate)
            {
                if (ch == Path.DirectorySeparatorChar
                    || ch == Path.AltDirectorySeparatorChar
                    || ch == '\0'
                    || invalidChars.Contains(ch))
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(ch);
                }
            }

            candidate = builder.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(candidate)
                || string.Equals(candidate, ".", StringComparison.Ordinal)
                || string.Equals(candidate, "..", StringComparison.Ordinal))
            {
                storageFolderName = string.Empty;
                return false;
            }

            storageFolderName = candidate;
            return true;
        }

        public static bool TryBuildPathWithinRoot(string rootPath, string childName, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(childName))
            {
                return false;
            }

            try
            {
                string normalizedRoot = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, childName))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!IsPathInsideRoot(candidate, normalizedRoot))
                {
                    return false;
                }

                fullPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsPathInsideRoot(string candidatePath, string rootPath)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedCandidate = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return normalizedCandidate.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryResolveBackupStoragePaths(
            string destinationRoot,
            string folderDisplayName,
            string? fallbackPath,
            out string storageFolderName,
            out string backupSubDir,
            out string metadataDir)
        {
            storageFolderName = string.Empty;
            backupSubDir = string.Empty;
            metadataDir = string.Empty;

            if (!TryResolveStorageFolderName(folderDisplayName, fallbackPath, out storageFolderName))
            {
                return false;
            }

            if (!TryBuildPathWithinRoot(destinationRoot, storageFolderName, out backupSubDir))
            {
                return false;
            }

            string metadataRoot = Path.Combine(destinationRoot, "_metadata");
            if (!TryBuildPathWithinRoot(metadataRoot, storageFolderName, out metadataDir))
            {
                return false;
            }

            return true;
        }
    }
}
