using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class FolderRenameService
{
    private static readonly SemaphoreSlim RenameGate = new(1, 1);

    private static readonly string[] WindowsReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static FolderRenamePreview PreviewRename(ManagedFolder folder, string newLeafName)
        => BuildRenamePreview(folder, newLeafName, cachedImpact: null);

    internal static FolderRenamePreview PreviewRenameWithCachedImpact(
        ManagedFolder folder,
        string newLeafName,
        FolderRenamePreview cachedImpact)
        => BuildRenamePreview(folder, newLeafName, cachedImpact);

    private static FolderRenamePreview BuildRenamePreview(
        ManagedFolder folder,
        string newLeafName,
        FolderRenamePreview? cachedImpact)
    {
        string oldPath = folder?.Path?.Trim() ?? string.Empty;
        string oldPathWithoutTrailingSeparator = TrimTrailingPathSeparators(oldPath);
        string rawNewLeaf = newLeafName ?? string.Empty;
        string normalizedNewLeaf = rawNewLeaf.Trim();
        string oldLeaf = string.IsNullOrWhiteSpace(oldPath)
            ? string.Empty
            : Path.GetFileName(oldPathWithoutTrailingSeparator);

        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(normalizedNewLeaf))
        {
            return InvalidPreview(
                "Source path or new folder name is empty.",
                oldPath,
                oldLeaf,
                normalizedNewLeaf);
        }

        if (IsInvalidWindowsLeafName(rawNewLeaf, normalizedNewLeaf))
        {
            return InvalidPreview(
                $"Folder name '{normalizedNewLeaf}' is invalid.",
                oldPath,
                oldLeaf,
                normalizedNewLeaf);
        }

        if (!TryResolveRenameablePath(oldPathWithoutTrailingSeparator, out oldLeaf, out string parent))
        {
            return InvalidPreview(
                "Source path does not contain a renameable folder name.",
                oldPath,
                oldLeaf,
                normalizedNewLeaf);
        }

        string newPath = Path.Combine(parent, normalizedNewLeaf);
        string currentDisplayName = folder?.DisplayName ?? string.Empty;
        string updatedDisplayName = ResolveUpdatedDisplayName(currentDisplayName, oldLeaf, normalizedNewLeaf);
        BackupStoragePathService.TryResolveStorageFolderName(
            currentDisplayName,
            oldPath,
            out string oldStorageFolderName);
        BackupStoragePathService.TryResolveStorageFolderName(
            updatedDisplayName,
            newPath,
            out string newStorageFolderName);

        int affectedReferenceCount;
        int affectedHistoryCount;
        if (cachedImpact != null)
        {
            affectedReferenceCount = cachedImpact.AffectedConfigCount;
            affectedHistoryCount = cachedImpact.AffectedHistoryCount;
        }
        else
        {
            HistoryService.Initialize();
            var affectedConfigs = ConfigService.CurrentConfig?.BackupConfigs?
                .Where(config => config?.SourceFolders != null)
                .ToList() ?? [];
            affectedReferenceCount = affectedConfigs
                .SelectMany(config => config.SourceFolders)
                .Count(item => item != null && AreSamePath(item.Path, oldPath));
            affectedHistoryCount = affectedConfigs
                .SelectMany(config => HistoryService.GetEntriesForConfig(config.Id))
                .Count(item => AreSamePath(item.FolderPath, oldPath));
        }
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
            AffectedConfigCount = affectedReferenceCount,
            AffectedHistoryCount = affectedHistoryCount
        };
    }

    public static string ResolveUpdatedDisplayName(
        string currentDisplayName,
        string oldLeafName,
        string newLeafName)
        => string.Equals(
            (currentDisplayName ?? string.Empty).Trim(),
            oldLeafName,
            StringComparison.OrdinalIgnoreCase)
            ? newLeafName
            : currentDisplayName ?? string.Empty;

    public static string ResolveUpdatedHistoryFolderName(
        string currentHistoryFolderName,
        string oldStorageFolderName,
        string newStorageFolderName)
        => string.Equals(
            (currentHistoryFolderName ?? string.Empty).Trim(),
            oldStorageFolderName,
            StringComparison.OrdinalIgnoreCase)
            ? newStorageFolderName
            : currentHistoryFolderName ?? string.Empty;

    internal static bool TryResolveHistoryIdentityUpdate(
        string configId,
        string folderPath,
        string folderName,
        IReadOnlyList<FolderRenameReferencePlan> references,
        out string newPath,
        out string newFolderName)
    {
        var matchingReferences = (references ?? Array.Empty<FolderRenameReferencePlan>())
            .Where(reference =>
                string.Equals(
                    configId,
                    reference.ConfigId,
                    StringComparison.OrdinalIgnoreCase)
                && AreSamePath(folderPath, reference.OldPath))
            .ToList();
        if (matchingReferences.Count == 0)
        {
            newPath = folderPath ?? string.Empty;
            newFolderName = folderName ?? string.Empty;
            return false;
        }

        newPath = matchingReferences[0].NewPath;
        var identityReference = matchingReferences.FirstOrDefault(reference =>
            string.Equals(
                folderName?.Trim(),
                reference.OldStorageFolderName,
                StringComparison.OrdinalIgnoreCase));
        newFolderName = identityReference?.NewStorageFolderName
            ?? folderName
            ?? string.Empty;
        return true;
    }


    private static bool TryBuildReferencePlans(
        FolderRenamePreview preview,
        out IReadOnlyList<FolderRenameReferencePlan> references,
        out string error)
    {
        var result = new List<FolderRenameReferencePlan>();
        foreach (var config in ConfigService.CurrentConfig?.BackupConfigs ?? [])
        {
            if (config?.SourceFolders == null)
            {
                continue;
            }

            foreach (var managedFolder in config.SourceFolders
                         .Where(item => item != null && AreSamePath(item.Path, preview.OldPath)))
            {
                string oldDisplayName = managedFolder.DisplayName ?? string.Empty;
                string newDisplayName = ResolveUpdatedDisplayName(
                    oldDisplayName,
                    preview.OldLeafName,
                    preview.NewLeafName);
                if (!BackupStoragePathService.TryResolveStorageFolderName(
                        oldDisplayName,
                        preview.OldPath,
                        out string oldStorageFolderName)
                    || !BackupStoragePathService.TryResolveStorageFolderName(
                        newDisplayName,
                        preview.NewPath,
                        out string newStorageFolderName))
                {
                    references = Array.Empty<FolderRenameReferencePlan>();
                    error = $"Cannot resolve backup storage identity for config '{config.Id}'.";
                    return false;
                }

                result.Add(new FolderRenameReferencePlan
                {
                    ConfigId = config.Id,
                    OldPath = preview.OldPath,
                    NewPath = preview.NewPath,
                    OldDisplayName = oldDisplayName,
                    NewDisplayName = newDisplayName,
                    OldStorageFolderName = oldStorageFolderName,
                    NewStorageFolderName = newStorageFolderName,
                    Config = config,
                    Folder = managedFolder
                });
            }
        }

        references = result;
        error = result.Count == 0
            ? "The selected folder is not referenced by any backup config."
            : string.Empty;
        return result.Count > 0;
    }

    private static IReadOnlyList<FolderMoveOperation> BuildMovePlan(
        IReadOnlyList<FolderRenameReferencePlan> references,
        string sourcePath,
        string destinationPath)
    {
        var operations = new List<FolderMoveOperation>
        {
            new()
            {
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Kind = FolderMoveOperationKind.SourceFolder
            }
        };

        foreach (var reference in references)
        {
            AddLocalMoveIfPresent(
                operations,
                reference,
                childDirectory: null,
                FolderMoveOperationKind.BackupDirectory);
            AddLocalMoveIfPresent(
                operations,
                reference,
                "_metadata",
                FolderMoveOperationKind.MetadataDirectory);
        }

        return operations
            .GroupBy(
                operation => (
                    NormalizePathForComparison(operation.SourcePath),
                    NormalizePathForComparison(operation.DestinationPath)),
                PathPairComparer.Instance)
            .Select(group => group.First())
            .ToArray();
    }

    private static void AddLocalMoveIfPresent(
        ICollection<FolderMoveOperation> operations,
        FolderRenameReferencePlan reference,
        string? childDirectory,
        FolderMoveOperationKind kind)
    {
        if (string.IsNullOrWhiteSpace(reference.Config.DestinationPath)
            || string.Equals(
                reference.OldStorageFolderName,
                reference.NewStorageFolderName,
                StringComparison.OrdinalIgnoreCase)
            || !TryResolveMoveRoot(reference.Config.DestinationPath, childDirectory, out string rootPath)
            || !BackupStoragePathService.TryBuildPathWithinRoot(
                rootPath,
                reference.OldStorageFolderName,
                out string sourcePath)
            || !Directory.Exists(sourcePath)
            || !BackupStoragePathService.TryBuildPathWithinRoot(
                rootPath,
                reference.NewStorageFolderName,
                out string destinationPath)
            || AreSamePath(sourcePath, destinationPath))
        {
            return;
        }

        operations.Add(new FolderMoveOperation
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Kind = kind,
            ConfigId = reference.ConfigId
        });
    }

}
