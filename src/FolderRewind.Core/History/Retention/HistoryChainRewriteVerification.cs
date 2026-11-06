using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace FolderRewind.History.Retention;

internal static class HistoryChainRewriteVerification
{
    internal static ArchiveRestoreVerificationContext? Create(IEnumerable<string> paths)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!IsLocalPath(path)) return null;
            var full = Path.GetFullPath(path);
            roots.Add(Path.GetPathRoot(full)!);
            for (var current = full; current is not null && checkedPaths.Add(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    // A local-looking alias can resolve to a network or unsupported volume.
                    // Keep legacy behavior unless the complete protection path is eligible.
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            }
        }
        foreach (var root in roots)
        {
            try
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)
                    || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
        // Capability fallback happens before acquiring any guard. Once verification
        // starts, a binding/protection failure must propagate instead of weakening it.
        return new(RestoreArchivePreflightMode.Strict) { UseRestoreMaterialization = false };
    }

    internal static bool IsLocalPath(string path)
        => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal);
}
