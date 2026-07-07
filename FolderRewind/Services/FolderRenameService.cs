using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

public static class FolderRenameService
{
    public static FolderRenamePreview PreviewRename(ManagedFolder folder, string newLeafName)
    {
        string oldPath = folder?.Path?.Trim() ?? string.Empty;
        string normalizedNewLeaf = (newLeafName ?? string.Empty).Trim();
        string oldLeaf = string.IsNullOrWhiteSpace(oldPath)
            ? string.Empty
            : Path.GetFileName(oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

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

        if (normalizedNewLeaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalizedNewLeaf is "." or "..")
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

        string parent = Path.GetDirectoryName(oldPath) ?? string.Empty;
        string newPath = Path.Combine(parent, normalizedNewLeaf);
        BackupStoragePathService.TryResolveStorageFolderName(oldLeaf, oldPath, out string oldStorageFolderName);
        BackupStoragePathService.TryResolveStorageFolderName(normalizedNewLeaf, newPath, out string newStorageFolderName);
        HistoryService.Initialize();

        int affectedConfigCount = ConfigService.CurrentConfig?.BackupConfigs?
            .SelectMany(config => config.SourceFolders)
            .Count(item => string.Equals(item.Path, oldPath, StringComparison.OrdinalIgnoreCase)) ?? 0;

        int affectedHistoryCount = ConfigService.CurrentConfig?.BackupConfigs?
            .SelectMany(config => HistoryService.GetEntriesForConfig(config.Id))
            .Count(item => string.Equals(item.FolderPath, oldPath, StringComparison.OrdinalIgnoreCase)) ?? 0;

        return new FolderRenamePreview
        {
            IsValid = !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase),
            Message = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
                ? "New folder name must differ from the current name."
                : string.Empty,
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
}
