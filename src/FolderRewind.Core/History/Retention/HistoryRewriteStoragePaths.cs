using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.IO;
using FolderRewind.Plugin.Runtime.IO;

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

    internal static IDisposable BindStorage(string path, string backupRoot)
    {
        var lease = DirectoryPathLease.BindExistingAncestor(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            DirectoryPathBoundary.RequireUnlinkedPath(path, backupRoot);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
}
