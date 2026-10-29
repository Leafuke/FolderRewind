using System;

namespace FolderRewind.Services;

/// <summary>Reserved Host storage, excluded independently of user filters or archive extensions.</summary>
internal static class PreviewStoragePaths
{
    internal const string DirectoryName = ".folderrewind-preview";
    internal static bool IsReserved(string path)
        => Array.Exists(path.Replace('\\', '/').Split('/'), p => string.Equals(p, DirectoryName, StringComparison.OrdinalIgnoreCase));
}
