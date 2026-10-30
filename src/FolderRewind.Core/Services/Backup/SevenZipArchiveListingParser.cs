using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderRewind.Services;

internal sealed record ArchiveFileListingEntry(string RelativePath, long Size);
internal sealed record ArchiveSafeListing(IReadOnlyDictionary<string, ArchiveFileListingEntry> Files, IReadOnlyList<string> Directories);

internal enum ArchiveListingParseFailureReason
{
    EmptyOutput,
    MissingEntrySection,
    MissingClassification,
    UnsupportedAttributes,
    InvalidFolderValue,
    ClassificationConflict,
    InvalidSize,
    UnsafePath,
    DuplicatePath,
    ParentFileConflict,
    UnsupportedEntryType
}

internal sealed record ArchiveListingParseFailure(
    ArchiveListingParseFailureReason Reason, int EntryIndex, string Field, string ValuePreview)
{
    internal static ArchiveListingParseFailure Create(ArchiveListingParseFailureReason reason,
        int entryIndex, string field, string? value)
    {
        // Only classification/size values are diagnostic data. Paths and link targets stay private.
        if (field is not ("Attributes" or "Folder" or "Size"))
            return new(reason, entryIndex, field, $"[redacted; length={value?.Length ?? 0}]");

        var preview = new StringBuilder();
        foreach (char character in value ?? string.Empty)
        {
            string escaped = character switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\\' => "\\\\",
                '"' => "\\\"",
                _ when char.IsControl(character) || char.IsSurrogate(character)
                    || char.GetUnicodeCategory(character) == UnicodeCategory.Format => $"\\u{(int)character:X4}",
                _ => character.ToString()
            };
            if (preview.Length + escaped.Length > 79)
            {
                preview.Append('…');
                break;
            }
            preview.Append(escaped);
        }
        return new(reason, entryIndex, field, preview.ToString());
    }

    public override string ToString()
        => $"Reason={Reason}; EntryIndex={EntryIndex}; Field={Field}; Value=\"{ValuePreview}\"";
}

/// <summary>
/// Parses the machine-oriented output of <c>7z l -slt</c> into files and directories;
/// the archive header is ignored. Current 7-Zip builds classify entries through
/// <c>Attributes</c>; some compatible builds also emit the older <c>Folder</c> field. Unsafe, ambiguous,
/// contradictory, or duplicate paths invalidate the listing.
/// </summary>
internal static class SevenZipArchiveListingParser
{
    public static bool TryParse(string? output, out IReadOnlyDictionary<string, ArchiveFileListingEntry> entries)
        => TryParse(output, out entries, out _);

    public static bool TryParse(string? output, out IReadOnlyDictionary<string, ArchiveFileListingEntry> entries,
        out IReadOnlyList<string> directories)
        => TryParse(output, out entries, out directories, out _);

    public static bool TryParse(string? output, out IReadOnlyDictionary<string, ArchiveFileListingEntry> entries,
        out IReadOnlyList<string> directories, out ArchiveListingParseFailure? failure)
    {
        var parsed = new Dictionary<string, ArchiveFileListingEntry>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ArchiveListingParseFailure? firstFailure = null;
        string? path = null;
        long? size = null;
        bool? isFolder = null;
        int entryIndex = 0;

        bool Fail(ArchiveListingParseFailureReason reason, string field, string? value, int? index = null)
        {
            firstFailure ??= ArchiveListingParseFailure.Create(reason, index ?? entryIndex, field, value);
            return false;
        }

        bool Flush()
        {
            if (path == null)
            {
                size = null;
                isFolder = null;
                return true;
            }

            if (!isFolder.HasValue) return Fail(ArchiveListingParseFailureReason.MissingClassification, "Folder", null);
            // 目录项也会被解压，不能因为不参与文件集合比较就忽略越界路径。
            if (!TryNormalizeRelativePath(path, out var normalized)) return Fail(ArchiveListingParseFailureReason.UnsafePath, "Path", path);
            if (!paths.TryAdd(normalized, entryIndex)) return Fail(ArchiveListingParseFailureReason.DuplicatePath, "Path", path);
            if (isFolder == false)
            {
                if (!size.HasValue) return Fail(ArchiveListingParseFailureReason.InvalidSize, "Size", null);
                parsed.Add(normalized, new ArchiveFileListingEntry(normalized, size.Value));
            }
            else folders.Add(normalized);

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

        bool Parse()
        {
            if (string.IsNullOrWhiteSpace(output)) return Fail(ArchiveListingParseFailureReason.EmptyOutput, "Output", null, 0);
            bool inEntries = false;
            using var reader = new StringReader(output);
            while (reader.ReadLine() is { } line)
            {
                if (!inEntries)
                {
                    if (line.StartsWith("----------", StringComparison.Ordinal)
                        && line.AsSpan().IndexOfAnyExcept('-') < 0) inEntries = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!Flush()) return false;
                    continue;
                }

                int separator = line.IndexOf(" =", StringComparison.Ordinal);
                if (separator <= 0) continue;
                int valueStart = separator + 2;
                if (valueStart < line.Length)
                {
                    if (line[valueStart] != ' ') continue;
                    valueStart++; // Remove exactly one separator space, preserving leading path spaces.
                }
                string key = line[..separator];
                string value = line[valueStart..];
                if (path is null && key is ("Size" or "Folder" or "Attributes" or "Symbolic Link" or "Hard Link" or "Alternate Stream"))
                    return Fail(ArchiveListingParseFailureReason.UnsafePath, "Path", null, entryIndex + 1);
                switch (key)
                {
                    case "Symbolic Link":
                    case "Hard Link":
                        return Fail(ArchiveListingParseFailureReason.UnsupportedEntryType, key, value);
                    case "Alternate Stream" when value == "+":
                        return Fail(ArchiveListingParseFailureReason.UnsupportedEntryType, key, value);
                    case "Path":
                        if (!Flush()) return false;
                        entryIndex++;
                        path = value;
                        break;
                    case "Size":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize)
                            || parsedSize < 0 || (size.HasValue && size.Value != parsedSize))
                        {
                            return Fail(ArchiveListingParseFailureReason.InvalidSize, key, value);
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
                        if (!folderClassification.HasValue) return Fail(ArchiveListingParseFailureReason.InvalidFolderValue, key, value);
                        if (!SetFolderClassification(folderClassification.Value)) return Fail(ArchiveListingParseFailureReason.ClassificationConflict, key, value);
                        break;
                    case "Attributes":
                        if (!TryClassifyAttributes(value, out var attributesAreFolder, out var reason)) return Fail(reason, key, value);
                        if (attributesAreFolder.HasValue && !SetFolderClassification(attributesAreFolder.Value))
                            return Fail(ArchiveListingParseFailureReason.ClassificationConflict, key, value);
                        break;
                }
            }

            if (!inEntries) return Fail(ArchiveListingParseFailureReason.MissingEntrySection, "Output", null, 0);
            if (!Flush()) return false;
            foreach (var (entry, ordinal) in paths)
            {
                var parent = entry;
                while (parent.LastIndexOf('/') is var index && index >= 0)
                {
                    parent = parent[..index];
                    if (parsed.ContainsKey(parent)) return Fail(ArchiveListingParseFailureReason.ParentFileConflict, "Path", entry, ordinal);
                }
            }
            return true;
        }

        bool success = Parse();
        entries = success ? parsed : new Dictionary<string, ArchiveFileListingEntry>(StringComparer.OrdinalIgnoreCase);
        directories = success ? folders : Array.Empty<string>();
        failure = firstFailure;
        return success;
    }

    private static bool TryClassifyAttributes(string value, out bool? isFolder, out ArchiveListingParseFailureReason reason)
    {
        isFolder = null;
        reason = ArchiveListingParseFailureReason.UnsupportedAttributes;
        var attributes = value.Trim();
        if (attributes.Length == 0) return true; // No evidence; an explicit Folder field may classify it.
        var tokens = attributes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1 && tokens[0].Length == 10)
        {
            if (TryClassifyPosix(tokens[0], out isFolder, out reason)) return true;
            if (reason == ArchiveListingParseFailureReason.UnsupportedEntryType) return false;
        }
        if (tokens.Length is < 1 or > 2) return false;

        string windowsFlags = tokens[0];
        if (windowsFlags.EndsWith('_'))
        {
            if (tokens.Length != 2) return false;
            windowsFlags = windowsFlags[..^1]; // Older 7-Zip UNIX-extension marker.
        }
        if (windowsFlags.Length == 0) return false;
        if (windowsFlags.Contains('.'))
        {
            const string positions = "DRHSA";
            if (windowsFlags.Length != positions.Length) return false;
            for (int index = 0; index < positions.Length; index++)
                if (windowsFlags[index] != '.' && windowsFlags[index] != positions[index]) return false;
        }
        else
        {
            // ConvertWinAttribToString (7-Zip PropIDUtils.cpp). Case matters: 'd' is a device,
            // 'D' a directory, 's' sparse and 'S' system. Unknown extension tokens stay rejected.
            const string knownFlags = "RHS8DAdNTsLCOIEVvXPUMB";
            foreach (char flag in windowsFlags)
                if (!knownFlags.Contains(flag)) return false;
            if (windowsFlags.IndexOfAny(['8', 'd', 'L']) >= 0)
            {
                reason = ArchiveListingParseFailureReason.UnsupportedEntryType;
                return false;
            }
        }
        isFolder = windowsFlags.Contains('D');
        if (tokens.Length == 1) return true;
        if (!TryClassifyPosix(tokens[1], out var posixIsFolder, out reason)) return false;
        if (isFolder != posixIsFolder)
        {
            reason = ArchiveListingParseFailureReason.ClassificationConflict;
            return false;
        }
        return true;
    }

    private static bool TryClassifyPosix(string permissions, out bool? isFolder, out ArchiveListingParseFailureReason reason)
    {
        isFolder = null;
        reason = ArchiveListingParseFailureReason.UnsupportedAttributes;
        if (permissions.Length != 10) return false;
        for (int index = 1; index < 10; index++)
        {
            char flag = permissions[index];
            bool valid = (index % 3) switch
            {
                1 => flag is 'r' or '-',
                2 => flag is 'w' or '-',
                _ => flag is 'x' or '-' || (index == 9 ? flag is 't' or 'T' : flag is 's' or 'S')
            };
            if (!valid) return false;
        }
        if (permissions[0] is not ('-' or 'd'))
        {
            reason = ArchiveListingParseFailureReason.UnsupportedEntryType;
            return false;
        }
        isFolder = permissions[0] == 'd';
        return true;
    }

    private static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        // normalized = value.Replace('/', '\\').Trim();
        // while (normalized.StartsWith(".\\", StringComparison.Ordinal))
        normalized = value.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return false;

        // foreach (var segment in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')) return false;
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
