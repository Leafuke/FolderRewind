using System;
using System.IO;

namespace FolderRewind.Services.Discovery;

public static class DiscoveryPathPolicy
{
    public static bool TryNormalizeAbsolutePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path.IndexOfAny(['*', '?']) >= 0
            || path.AsSpan(2).Contains(':')) return false;
        try
        {
            var full = Path.GetFullPath(path);
            // A drive-relative path such as C: must never replace the absolute root C:\.
            var root = Path.GetPathRoot(full)!;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)
                && root.Trim('\\').Split('\\').Length < 2) return false;
            normalized = string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar
                : Path.TrimEndingDirectorySeparator(full);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool Equals(string? left, string? right) =>
        TryNormalizeAbsolutePath(left, out var a) && TryNormalizeAbsolutePath(right, out var b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool IsValidOrReport(string? path)
    {
        if (TryNormalizeAbsolutePath(path, out _)) return true;
        LogService.LogWarning($"Skipping invalid discovery path: '{path}'.", nameof(DiscoveryPathPolicy));
        return false;
    }
}

/// <summary>Frozen encoding used in existing Ludusavi installation/resource IDs. Not a path validator.</summary>
internal static class DiscoveryIdentityPathV1
{
    internal static string Encode(string value)
    {
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return value.Trim(); }
    }
}
