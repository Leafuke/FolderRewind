using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FolderRewind.Services;

internal sealed record ArchiveFileListingEntry(string RelativePath, long Size);

/// <summary>
/// 解析 <c>7z l -slt</c> 的机器可读输出。只保留文件条目，目录条目与归档头一并忽略。
/// 当前 7-Zip 通过 <c>Attributes</c> 分类条目，部分兼容实现仍输出旧的 <c>Folder</c> 字段。
/// 出现不安全、歧义、自相矛盾或重复的路径时，整份清单判为无效。
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

        // Windows 上的 7-Zip 会输出 "A"、"R"、"D" 这类值；
        // 携带 POSIX 属性的归档则可能以 '-'（文件）或 'd'（目录）开头。
        var first = attributes[0];
        isFolder = first is 'D' or 'd';
        return first is '-' or 'D' or 'd' or 'A' or 'a' or 'R' or 'r' or 'H' or 'h' or 'S' or 's';
    }

    private static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        normalized = value.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return false;

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }

        return normalized.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}

/// <summary>
/// 比对归档清单与捕获时冻结的文件状态：两个方向的条目数与逐个文件的大小都必须一致。
/// </summary>
internal static class ArchiveLogicalStateVerifier
{
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
