using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        if (!TryResolveRenameablePath(oldPathWithoutTrailingSeparator, out oldLeaf, out string parent))
        {
            return new FolderRenamePreview
            {
                IsValid = false,
                Message = "Source path does not contain a renameable folder name.",
                OldPath = oldPath,
                OldLeafName = oldLeaf,
                NewLeafName = normalizedNewLeaf
            };
        }

        string newPath = Path.Combine(parent, normalizedNewLeaf);
        string currentDisplayName = folder?.DisplayName ?? string.Empty;
        string updatedDisplayName = ResolveUpdatedDisplayName(currentDisplayName, oldLeaf, normalizedNewLeaf);
        BackupStoragePathService.TryResolveStorageFolderName(currentDisplayName, oldPath, out string oldStorageFolderName);
        BackupStoragePathService.TryResolveStorageFolderName(updatedDisplayName, newPath, out string newStorageFolderName);
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

    public static Task<FolderRenameResult> RenameAsync(ManagedFolder folder, string newLeafName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preview = PreviewRename(folder, newLeafName);
        if (!preview.IsValid)
        {
            return Task.FromResult(new FolderRenameResult
            {
                Success = false,
                Message = preview.Message,
                OldPath = preview.OldPath,
                NewPath = preview.NewPath,
                AffectedConfigCount = preview.AffectedConfigCount,
                AffectedHistoryCount = preview.AffectedHistoryCount
            });
        }

        string sourcePath = TrimTrailingPathSeparators(preview.OldPath);
        string destinationPath = TrimTrailingPathSeparators(preview.NewPath);
        if (!Directory.Exists(sourcePath))
        {
            return Task.FromResult(new FolderRenameResult
            {
                Success = false,
                Message = $"Source folder does not exist: {preview.OldPath}",
                OldPath = preview.OldPath,
                NewPath = preview.NewPath,
                AffectedConfigCount = preview.AffectedConfigCount,
                AffectedHistoryCount = preview.AffectedHistoryCount
            });
        }

        var configs = ConfigService.CurrentConfig?.BackupConfigs?
            .Where(config => config?.SourceFolders?.Any(item => AreSamePath(item?.Path, preview.OldPath)) == true)
            .ToList() ?? new List<BackupConfig>();

        CloseRenameDependents(preview.OldPath);

        var operations = new List<FolderMoveOperation>
        {
            new()
            {
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Description = "source folder"
            }
        };

        foreach (var config in configs)
        {
            if (TryBuildLocalMoveOperation(config, preview.OldStorageFolderName, preview.NewStorageFolderName, childDirectory: null, out var backupMove))
            {
                AddDistinctMoveOperation(operations, backupMove);
            }

            if (TryBuildLocalMoveOperation(config, preview.OldStorageFolderName, preview.NewStorageFolderName, "_metadata", out var metadataMove))
            {
                AddDistinctMoveOperation(operations, metadataMove);
            }
        }

        var moveResult = ExecuteMovePlan(operations);
        if (!moveResult.Success)
        {
            return Task.FromResult(new FolderRenameResult
            {
                Success = false,
                Message = moveResult.Message,
                OldPath = preview.OldPath,
                NewPath = preview.NewPath,
                AffectedConfigCount = configs.Count,
                AffectedHistoryCount = preview.AffectedHistoryCount,
                LocalBackupDirectoryMigrated = moveResult.LocalBackupDirectoryMigrated,
                LocalMetadataDirectoryMigrated = moveResult.LocalMetadataDirectoryMigrated
            });
        }

        if (folder != null)
        {
            folder.Path = preview.NewPath;
            folder.DisplayName = ResolveUpdatedDisplayName(folder.DisplayName, preview.OldLeafName, preview.NewLeafName);
        }

        ApplyReferenceUpdates(
            configs,
            ConfigService.CurrentConfig?.GlobalSettings ?? new GlobalSettings(),
            Array.Empty<HistoryItem>(),
            preview);

        int affectedHistoryCount = HistoryService.UpdateFolderIdentity(
            preview.OldPath,
            preview.NewPath,
            preview.OldStorageFolderName,
            preview.NewStorageFolderName);

        ConfigService.Save();

        return Task.FromResult(new FolderRenameResult
        {
            Success = true,
            Message = "Folder renamed successfully.",
            OldPath = preview.OldPath,
            NewPath = preview.NewPath,
            AffectedConfigCount = configs.Count,
            AffectedHistoryCount = affectedHistoryCount,
            LocalBackupDirectoryMigrated = moveResult.LocalBackupDirectoryMigrated,
            LocalMetadataDirectoryMigrated = moveResult.LocalMetadataDirectoryMigrated
        });
    }

    public static void ApplyReferenceUpdates(IEnumerable<BackupConfig> configs, GlobalSettings settings, IList<HistoryItem> historyItems, FolderRenamePreview preview)
    {
        foreach (var config in configs ?? Enumerable.Empty<BackupConfig>())
        {
            if (config?.SourceFolders != null)
            {
                foreach (var managedFolder in config.SourceFolders.Where(item => item != null && AreSamePath(item.Path, preview.OldPath)))
                {
                    managedFolder.Path = preview.NewPath;
                    managedFolder.DisplayName = ResolveUpdatedDisplayName(managedFolder.DisplayName, preview.OldLeafName, preview.NewLeafName);
                }
            }

            if (config?.Automation != null
                && AreSamePath(config.Automation.TargetFolderPath, preview.OldPath))
            {
                config.Automation.TargetFolderPath = preview.NewPath;
            }
        }

        if (settings != null)
        {
            if (AreSamePath(settings.LastManagerFolderPath, preview.OldPath))
            {
                settings.LastManagerFolderPath = preview.NewPath;
            }

            if (AreSamePath(settings.LastHistoryFolderPath, preview.OldPath))
            {
                settings.LastHistoryFolderPath = preview.NewPath;
            }
        }

        foreach (var item in historyItems ?? Array.Empty<HistoryItem>())
        {
            if (item == null || !AreSamePath(item.FolderPath, preview.OldPath))
            {
                continue;
            }

            item.FolderPath = preview.NewPath;
            item.FolderName = ResolveUpdatedHistoryFolderName(item.FolderName, preview.OldStorageFolderName, preview.NewStorageFolderName);
        }
    }

    public static FolderRenameResult ExecuteMovePlan(IReadOnlyList<FolderMoveOperation> operations)
    {
        bool backupDirectoryMoved = false;
        bool metadataDirectoryMoved = false;
        var completed = new Stack<FolderMoveOperation>();

        try
        {
            foreach (var operation in operations ?? Array.Empty<FolderMoveOperation>())
            {
                if (operation == null
                    || string.IsNullOrWhiteSpace(operation.SourcePath)
                    || string.IsNullOrWhiteSpace(operation.DestinationPath)
                    || AreSamePath(operation.SourcePath, operation.DestinationPath)
                    || !Directory.Exists(operation.SourcePath))
                {
                    continue;
                }

                if (Directory.Exists(operation.DestinationPath))
                {
                    throw new IOException($"Destination already exists for {operation.Description}: {operation.DestinationPath}");
                }

                Directory.Move(operation.SourcePath, operation.DestinationPath);
                completed.Push(operation);
                backupDirectoryMoved |= IsBackupOperation(operation);
                metadataDirectoryMoved |= IsMetadataOperation(operation);
            }

            return new FolderRenameResult
            {
                Success = true,
                LocalBackupDirectoryMigrated = backupDirectoryMoved,
                LocalMetadataDirectoryMigrated = metadataDirectoryMoved
            };
        }
        catch (Exception ex)
        {
            while (completed.Count > 0)
            {
                var operation = completed.Pop();
                try
                {
                    if (Directory.Exists(operation.DestinationPath) && !Directory.Exists(operation.SourcePath))
                    {
                        Directory.Move(operation.DestinationPath, operation.SourcePath);
                    }
                }
                catch
                {
                }
            }

            return new FolderRenameResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

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

    private static void CloseRenameDependents(string oldPath)
    {
        foreach (string candidate in GetPathCandidates(oldPath))
        {
            if (MiniWindowService.IsOpen(candidate))
            {
                MiniWindowService.Close(candidate);
            }

            FolderWatcherService.StopWatching(candidate);
        }
    }

    private static IEnumerable<string> GetPathCandidates(string path)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in new[]
            {
                path,
                TrimTrailingPathSeparators(path),
                NormalizePathForComparison(path)
            })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidates.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static void AddDistinctMoveOperation(ICollection<FolderMoveOperation> operations, FolderMoveOperation operation)
    {
        if (operations.Any(existing =>
                AreSamePath(existing.SourcePath, operation.SourcePath)
                && AreSamePath(existing.DestinationPath, operation.DestinationPath)))
        {
            return;
        }

        operations.Add(operation);
    }

    private static bool TryBuildLocalMoveOperation(
        BackupConfig config,
        string oldStorageFolderName,
        string newStorageFolderName,
        string? childDirectory,
        out FolderMoveOperation operation)
    {
        operation = null!;

        if (config == null
            || string.IsNullOrWhiteSpace(config.DestinationPath)
            || string.IsNullOrWhiteSpace(oldStorageFolderName)
            || string.IsNullOrWhiteSpace(newStorageFolderName)
            || string.Equals(oldStorageFolderName, newStorageFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryResolveMoveRoot(config.DestinationPath, childDirectory, out string rootPath)
            || !BackupStoragePathService.TryBuildPathWithinRoot(rootPath, oldStorageFolderName, out string sourcePath)
            || !Directory.Exists(sourcePath)
            || !BackupStoragePathService.TryBuildPathWithinRoot(rootPath, newStorageFolderName, out string destinationPath)
            || AreSamePath(sourcePath, destinationPath))
        {
            return false;
        }

        operation = new FolderMoveOperation
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Description = string.IsNullOrWhiteSpace(childDirectory) ? "backup directory" : "metadata directory"
        };
        return true;
    }

    private static bool TryResolveMoveRoot(string destinationPath, string? childDirectory, out string rootPath)
    {
        rootPath = string.Empty;
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(childDirectory))
            {
                rootPath = Path.GetFullPath(destinationPath);
                return true;
            }

            return BackupStoragePathService.TryBuildPathWithinRoot(destinationPath, childDirectory, out rootPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBackupOperation(FolderMoveOperation operation)
        => operation.Description?.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0
            && !IsMetadataOperation(operation);

    private static bool IsMetadataOperation(FolderMoveOperation operation)
        => operation.Description?.IndexOf("metadata", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryResolveRenameablePath(string path, out string oldLeaf, out string parent)
    {
        oldLeaf = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFileName(path);
        parent = Path.GetDirectoryName(path) ?? string.Empty;

        return !string.IsNullOrWhiteSpace(oldLeaf)
            && !string.IsNullOrWhiteSpace(parent)
            && !AreSamePath(path, parent);
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
