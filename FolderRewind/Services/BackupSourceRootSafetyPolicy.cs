using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

public static class BackupSourceRootSafetyPolicy
{
    public static bool IsBroadRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }
        string normalized;
        try
        {
            normalized = Normalize(path);
        }
        catch
        {
            return true;
        }

        var volumeRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || string.Equals(normalized, Normalize(volumeRoot), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return KnownBroadRoots().Any(root => PathsEqual(normalized, root));
    }

    private static IEnumerable<string> KnownBroadRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    private static bool PathsEqual(string left, string right) =>
        !string.IsNullOrWhiteSpace(right)
        && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
