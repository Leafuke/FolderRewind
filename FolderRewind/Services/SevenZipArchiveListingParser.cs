using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FolderRewind.Services;

internal sealed record ArchiveFileListingEntry(string RelativePath, long Size);

/// <summary>
/// Parses the machine-oriented output of <c>7z l -slt</c>. Only file entries are retained;
/// directory entries and the archive header are ignored. Current 7-Zip builds classify entries through
/// <c>Attributes</c>; some compatible builds also emit the older <c>Folder</c> field. Unsafe, ambiguous,
/// contradictory, or duplicate paths invalidate the listing.
/// </summary>
internal static class SevenZipArchiveListingParser
{
    public static bool TryParse(string? output, out IReadOnlyDictionary<string, ArchiveFileListingEntry> entries)
    {
        var parsed = new Dictionary<string, ArchiveFileListingEntry>(StringComparer.OrdinalIgnoreCase);
        entries = parsed;
        if (string.IsNullOrWhiteSpace(output)) return false;

        string? path = null;
        long? size = null;
        bool? isFolder = null;
        bool inEntries = false;

        bool Flush()
        {
            if (path == null)
            {
                size = null;
                isFolder = null;
                return true;
            }

            if (!isFolder.HasValue) return false;
            if (isFolder == false)
            {
                if (!size.HasValue || !TryNormalizeRelativePath(path, out var normalized)) return false;
                if (!parsed.TryAdd(normalized, new ArchiveFileListingEntry(normalized, size.Value))) return false;
            }

            path = null;
            size = null;
            isFolder = null;
            return true;
        }

        bool SetFolderClassification(bool value)
        {
            if (isFolder.HasValue && isFolder.Value != value) return false;
            isFolder = value;
            return true;
        }

        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (!inEntries)
            {
                if (line.StartsWith("----------", StringComparison.Ordinal)) inEntries = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (!Flush()) return false;
                continue;
            }

            int separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            string key = line[..separator];
            string value = line[(separator + 3)..];
            switch (key)
            {
                case "Path":
                    if (!Flush()) return false;
                    path = value;
                    break;
                case "Size":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize)
                        || parsedSize < 0)
                    {
                        return false;
                    }
                    size = parsedSize;
                    break;
                case "Folder":
                    var folderClassification = value switch
                    {
                        "+" => (bool?)true,
                        "-" => false,
                        _ => null
                    };
                    if (!folderClassification.HasValue
                        || !SetFolderClassification(folderClassification.Value)) return false;
                    break;
                case "Attributes":
                    if (!TryClassifyAttributes(value, out var attributesAreFolder)
                        || !SetFolderClassification(attributesAreFolder)) return false;
                    break;
            }
        }

        return Flush();
    }

    private static bool TryClassifyAttributes(string value, out bool isFolder)
    {
        isFolder = false;
        var attributes = value.Trim();
        if (attributes.Length == 0) return false;

        // 7-Zip on Windows emits values such as "A", "R", or "D". Archives carrying
        // POSIX attributes may instead start with '-' for files or 'd' for directories.
        var first = attributes[0];
        isFolder = first is 'D' or 'd';
        return first is '-' or 'D' or 'd' or 'A' or 'a' or 'R' or 'r' or 'H' or 'h' or 'S' or 's';
    }

    private static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        // normalized = value.Replace('/', '\\').Trim();
        // while (normalized.StartsWith(".\\", StringComparison.Ordinal))
        normalized = value.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return false;

        // foreach (var segment in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }

        return normalized.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}

internal static class ArchiveLogicalStateVerifier
{
    public static bool Matches(
        IReadOnlyDictionary<string, long> expectedFileSizes,
        IReadOnlyDictionary<string, ArchiveFileListingEntry> actualEntries)
        => TryMatch(expectedFileSizes, actualEntries, out _);

    public static bool TryMatch(
        IReadOnlyDictionary<string, long> expectedFileSizes,
        IReadOnlyDictionary<string, ArchiveFileListingEntry> actualEntries,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(expectedFileSizes);
        ArgumentNullException.ThrowIfNull(actualEntries);
        if (expectedFileSizes.Count != actualEntries.Count)
        {
            diagnostic = $"Archive file count mismatch: expected {expectedFileSizes.Count}, actual {actualEntries.Count}.";
            return false;
        }

        foreach (var (path, expectedSize) in expectedFileSizes)
        {
            if (!actualEntries.TryGetValue(path, out var actual))
            {
                diagnostic = $"Archive entry is missing: {path}";
                return false;
            }
            if (actual.Size != expectedSize)
            {
                diagnostic = $"Archive entry size mismatch for {path}: expected {expectedSize}, actual {actual.Size}.";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }
}
