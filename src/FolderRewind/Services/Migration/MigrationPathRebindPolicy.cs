using System;
using System.IO;

namespace FolderRewind.Services;

internal static class MigrationPathRebindPolicy
{
    internal static string ReplaceRoot(string path, string oldRoot, string newRoot)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(oldRoot) || !Path.IsPathFullyQualified(newRoot))
            throw new ArgumentException("Migration root mapping requires absolute paths.");
        var original = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot));
        if (string.Equals(Path.TrimEndingDirectorySeparator(original), root, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(newRoot);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return original.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(newRoot, original[prefix.Length..])) : path;
    }
}
