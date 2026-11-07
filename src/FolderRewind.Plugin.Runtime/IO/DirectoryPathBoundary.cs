namespace FolderRewind.Plugin.Runtime.IO;

public readonly record struct RedirectedPathEntry(string Path, int Depth, FileAttributes Attributes);

/// <summary>Checks the managed descendants of a separately bound, selected storage root.</summary>
public static class DirectoryPathBoundary
{
    public static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static RedirectedPathEntry? FindRedirectedEntry(string path, string trustedRoot)
    {
        path = Path.GetFullPath(path);
        trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        if (!Contains(trustedRoot, path)) throw new IOException("Path escapes its selected storage root.");
        var depth = 0;
        for (string? current = Path.TrimEndingDirectorySeparator(path);
             current is not null && !StringComparer.OrdinalIgnoreCase.Equals(current, trustedRoot);
             current = Path.GetDirectoryName(current), depth++)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return new(current, depth, attributes);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return null;
    }

    public static void RequireUnlinkedPath(string path, string trustedRoot)
    {
        if (FindRedirectedEntry(path, trustedRoot) is not null)
            throw new IOException("Managed storage contains a filesystem alias.");
    }
}
