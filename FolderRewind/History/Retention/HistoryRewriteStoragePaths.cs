using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.IO;

namespace FolderRewind.History.Retention;

/// <summary>Archive bytes belong to the configured backup volume; journals remain device-local.</summary>
public static class HistoryRewriteStoragePaths
{
    public static string NormalizeBackupRoot(string? backupRoot)
    {
        if (string.IsNullOrWhiteSpace(backupRoot) || !Path.IsPathFullyQualified(backupRoot))
            throw new ArgumentException("Safe backup rewriting requires an absolute backup destination.", nameof(backupRoot));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));
    }

    public static string ManagedRoot(string backupRoot, HistoryConfigId configId)
        => Path.Combine(NormalizeBackupRoot(backupRoot), ".folderrewind", "rewrites",
            HistoryRepositoryPaths.EncodeConfigPathSegment(configId));

    public static string OperationRoot(string backupRoot, HistoryConfigId configId, HistoryTransactionId operationId)
        => Path.Combine(ManagedRoot(backupRoot, configId), "rewrite-" + operationId);

    internal static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static void RequireUnlinkedAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if ((Directory.Exists(directory.FullName) || File.Exists(directory.FullName))
                && (File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Backup rewrite storage refuses linked directories.");
        }
    }
}
