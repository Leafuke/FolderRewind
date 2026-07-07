using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

public static class FolderRenameService
{
    private static readonly string[] WindowsReservedDeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    ];

    public static FolderRenamePreview PreviewRename(ManagedFolder folder, string newLeafName)
    {
        string oldPath = folder?.Path?.Trim() ?? string.Empty;
        string oldPathWithoutTrailingSeparator = TrimTrailingPathSeparators(oldPath);
        string rawNewLeaf = newLeafName ?? string.Empty;
        string normalizedNewLeaf = (newLeafName ?? string.Empty).Trim();
        string oldLeaf = string.IsNullOrWhiteSpace(oldPath)
            ? string.Empty
            : Path.GetFileName(oldPathWithoutTrailingSeparator);

        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(normalizedNewLeaf))
        {
            return new FolderRenamePreview
            {
                IsValid = false,
                Message = "Source path or new folder name is empty.",
                OldPath = oldPath,
                OldLeafName = oldLeaf
            };
        }

        if (IsInvalidWindowsLeafName(rawNewLeaf, normalizedNewLeaf))
        {
            return new FolderRenamePreview
            {
                IsValid = false,
                Message = $"Folder name '{normalizedNewLeaf}' is invalid.",
                OldPath = oldPath,
                OldLeafName = oldLeaf,
                NewLeafName = normalizedNewLeaf
            };
        }

        string parent = Path.GetDirectoryName(oldPathWithoutTrailingSeparator) ?? string.Empty;
        string newPath = Path.Combine(parent, normalizedNewLeaf);
        BackupStoragePathService.TryResolveStorageFolderName(oldLeaf, oldPath, out string oldStorageFolderName);
        BackupStoragePathService.TryResolveStorageFolderName(normalizedNewLeaf, newPath, out string newStorageFolderName);
        HistoryService.Initialize();

        int affectedConfigCount = ConfigService.CurrentConfig?.BackupConfigs?
            .SelectMany(config => config.SourceFolders)
            .Count(item => AreSamePath(item.Path, oldPath)) ?? 0;

        int affectedHistoryCount = ConfigService.CurrentConfig?.BackupConfigs?
            .SelectMany(config => HistoryService.GetEntriesForConfig(config.Id))
            .Count(item => AreSamePath(item.FolderPath, oldPath)) ?? 0;

        bool changesPath = !AreSamePath(oldPath, newPath);

        return new FolderRenamePreview
        {
            IsValid = changesPath,
            Message = changesPath
                ? string.Empty
                : "New folder name must differ from the current name.",
            OldPath = oldPath,
            NewPath = newPath,
            OldLeafName = oldLeaf,
            NewLeafName = normalizedNewLeaf,
            OldStorageFolderName = oldStorageFolderName,
            NewStorageFolderName = newStorageFolderName,
            AffectedConfigCount = affectedConfigCount,
            AffectedHistoryCount = affectedHistoryCount
        };
    }

    public static string ResolveUpdatedDisplayName(string currentDisplayName, string oldLeafName, string newLeafName)
        => string.Equals((currentDisplayName ?? string.Empty).Trim(), oldLeafName, StringComparison.OrdinalIgnoreCase)
            ? newLeafName
            : currentDisplayName ?? string.Empty;

    public static string ResolveUpdatedHistoryFolderName(string currentHistoryFolderName, string oldStorageFolderName, string newStorageFolderName)
        => string.Equals((currentHistoryFolderName ?? string.Empty).Trim(), oldStorageFolderName, StringComparison.OrdinalIgnoreCase)
            ? newStorageFolderName
            : currentHistoryFolderName ?? string.Empty;

    private static bool IsInvalidWindowsLeafName(string rawLeafName, string normalizedLeafName)
    {
        if (normalizedLeafName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalizedLeafName.IndexOf(Path.DirectorySeparatorChar) >= 0
            || normalizedLeafName.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || normalizedLeafName is "." or "..")
        {
            return true;
        }

        if (rawLeafName.EndsWith(' ') || rawLeafName.EndsWith('.'))
        {
            return true;
        }

        string reservedCandidate = normalizedLeafName;
        int extensionSeparator = reservedCandidate.IndexOf('.', StringComparison.Ordinal);
        if (extensionSeparator >= 0)
        {
            reservedCandidate = reservedCandidate[..extensionSeparator];
        }

        return WindowsReservedDeviceNames.Contains(reservedCandidate, StringComparer.OrdinalIgnoreCase);
    }

    private static bool AreSamePath(string? left, string? right)
    {
        string normalizedLeft = NormalizePathForComparison(left);
        string normalizedRight = NormalizePathForComparison(right);

        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForComparison(string? path)
    {
        string candidate = TrimTrailingPathSeparators((path ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return candidate;
        }
    }

    private static string TrimTrailingPathSeparators(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string root = Path.GetPathRoot(path) ?? string.Empty;
        string trimmed = path;
        while (trimmed.Length > root.Length
            && (trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}
