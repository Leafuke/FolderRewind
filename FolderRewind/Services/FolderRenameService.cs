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
    private static readonly SemaphoreSlim RenameGate = new(1, 1);

    private static readonly string[] WindowsReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static FolderRenamePreview PreviewRename(ManagedFolder folder, string newLeafName)
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

        HistoryService.Initialize();
        var affectedConfigs = ConfigService.CurrentConfig?.BackupConfigs?
            .Where(config => config?.SourceFolders != null)
            .ToList() ?? [];
        int affectedReferenceCount = affectedConfigs
            .SelectMany(config => config.SourceFolders)
            .Count(item => item != null && AreSamePath(item.Path, oldPath));
        int affectedHistoryCount = affectedConfigs
            .SelectMany(config => HistoryService.GetEntriesForConfig(config.Id))
            .Count(item => AreSamePath(item.FolderPath, oldPath));
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

    public static async Task<FolderRenameResult> RenameAsync(
        ManagedFolder folder,
        string newLeafName,
        CancellationToken cancellationToken = default)
    {
        await RenameGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = PreviewRename(folder, newLeafName);
            if (!preview.IsValid)
            {
                return Failed(preview, preview.Message);
            }

            string sourcePath = NormalizePathForComparison(preview.OldPath);
            string destinationPath = NormalizePathForComparison(preview.NewPath);
            if (!Directory.Exists(sourcePath))
            {
                return Failed(preview, $"Source folder does not exist: {preview.OldPath}");
            }

            if (!TryBuildReferencePlans(preview, out var references, out string planError))
            {
                return Failed(preview, planError);
            }

            var operations = BuildMovePlan(references, sourcePath, destinationPath);
            var conflicts = ValidateMovePlan(operations);
            if (conflicts.Count > 0)
            {
                return Failed(preview, conflicts[0], references.Count, conflicts);
            }

            var runtimeState = CaptureRuntimeState(references, preview.OldPath);
            CloseRenameDependents(preview.OldPath);

            var moveResult = ExecuteMovePlan(operations, cancellationToken);
            if (!moveResult.Success)
            {
                RestoreRuntimeState(runtimeState);
                return new FolderRenameResult
                {
                    Success = false,
                    Message = moveResult.Message,
                    OldPath = preview.OldPath,
                    NewPath = preview.NewPath,
                    AffectedConfigCount = references.Count,
                    AffectedHistoryCount = preview.AffectedHistoryCount,
                    LocalBackupDirectoryMigrated = moveResult.LocalBackupDirectoryMigrated,
                    LocalMetadataDirectoryMigrated = moveResult.LocalMetadataDirectoryMigrated,
                    Conflicts = moveResult.Conflicts,
                    RollbackSucceeded = moveResult.RollbackSucceeded,
                    RollbackErrors = moveResult.RollbackErrors
                };
            }

            ApplyReferenceUpdates(references);
            UpdateGlobalPathReferences(
                ConfigService.CurrentConfig?.GlobalSettings,
                preview.OldPath,
                preview.NewPath);
            int affectedHistoryCount = HistoryService.UpdateFolderIdentities(references);
            ConfigService.Save();
            RestoreRuntimeState(runtimeState);

            return new FolderRenameResult
            {
                Success = true,
                Message = "Folder renamed successfully.",
                OldPath = preview.OldPath,
                NewPath = preview.NewPath,
                AffectedConfigCount = references.Count,
                AffectedHistoryCount = affectedHistoryCount,
                LocalBackupDirectoryMigrated = moveResult.LocalBackupDirectoryMigrated,
                LocalMetadataDirectoryMigrated = moveResult.LocalMetadataDirectoryMigrated
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            RenameGate.Release();
        }
    }

    public static FolderRenameResult ExecuteMovePlan(
        IReadOnlyList<FolderMoveOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var conflicts = ValidateMovePlan(operations);
        if (conflicts.Count > 0)
        {
            return new FolderRenameResult
            {
                Success = false,
                Message = conflicts[0],
                Conflicts = conflicts
            };
        }

        bool backupDirectoryMoved = false;
        bool metadataDirectoryMoved = false;
        var completed = new Stack<FolderMoveOperation>();

        try
        {
            foreach (var operation in operations ?? Array.Empty<FolderMoveOperation>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(operation.SourcePath, operation.DestinationPath);
                completed.Push(operation);
                backupDirectoryMoved |= operation.Kind == FolderMoveOperationKind.BackupDirectory;
                metadataDirectoryMoved |= operation.Kind == FolderMoveOperationKind.MetadataDirectory;
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
            var rollbackErrors = RollbackMoves(completed);
            return new FolderRenameResult
            {
                Success = false,
                Message = ex.Message,
                LocalBackupDirectoryMigrated = backupDirectoryMoved,
                LocalMetadataDirectoryMigrated = metadataDirectoryMoved,
                RollbackSucceeded = rollbackErrors.Count == 0,
                RollbackErrors = rollbackErrors
            };
        }
    }

    public static IReadOnlyList<string> ValidateMovePlan(IReadOnlyList<FolderMoveOperation> operations)
    {
        var conflicts = new List<string>();
        var normalized = new List<(FolderMoveOperation Operation, string Source, string Destination)>();

        foreach (var operation in operations ?? Array.Empty<FolderMoveOperation>())
        {
            if (operation == null
                || string.IsNullOrWhiteSpace(operation.SourcePath)
                || string.IsNullOrWhiteSpace(operation.DestinationPath))
            {
                conflicts.Add("A rename move contains an empty path.");
                continue;
            }

            string source = NormalizePathForComparison(operation.SourcePath);
            string destination = NormalizePathForComparison(operation.DestinationPath);
            if (string.IsNullOrWhiteSpace(source)
                || string.IsNullOrWhiteSpace(destination)
                || AreSamePath(source, destination))
            {
                conflicts.Add($"Invalid rename move: '{operation.SourcePath}' -> '{operation.DestinationPath}'.");
                continue;
            }

            if (!Directory.Exists(source))
            {
                conflicts.Add($"Source directory does not exist: {source}");
            }

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                conflicts.Add($"Rename destination already exists: {destination}");
            }

            if (IsStrictDescendant(destination, source) || IsStrictDescendant(source, destination))
            {
                conflicts.Add($"Rename move crosses its own directory boundary: {source} -> {destination}");
            }

            normalized.Add((operation, source, destination));
        }

        foreach (var sourceGroup in normalized.GroupBy(
                     item => item.Source,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (sourceGroup.Select(item => item.Destination)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any())
            {
                conflicts.Add($"One source directory has multiple rename targets: {sourceGroup.Key}");
            }
        }

        foreach (var destinationGroup in normalized.GroupBy(
                     item => item.Destination,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (destinationGroup.Select(item => item.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any())
            {
                conflicts.Add($"Multiple directories target the same rename destination: {destinationGroup.Key}");
            }
        }

        for (int left = 0; left < normalized.Count; left++)
        {
            for (int right = left + 1; right < normalized.Count; right++)
            {
                var first = normalized[left];
                var second = normalized[right];
                if (AreSamePath(first.Source, second.Source)
                    && AreSamePath(first.Destination, second.Destination))
                {
                    continue;
                }

                if (AreSamePath(first.Destination, second.Source)
                    || AreSamePath(second.Destination, first.Source))
                {
                    conflicts.Add(
                        $"Rename moves form an occupied chain: {first.Source} -> {first.Destination}, "
                        + $"{second.Source} -> {second.Destination}");
                }

                if (IsStrictDescendant(first.Source, second.Source)
                    || IsStrictDescendant(second.Source, first.Source))
                {
                    conflicts.Add(
                        $"Rename source directories overlap: {first.Source}, {second.Source}");
                }
            }
        }

        return conflicts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static void ApplyReferenceUpdates(IReadOnlyList<FolderRenameReferencePlan> references)
    {
        foreach (var reference in references)
        {
            reference.Folder.Path = reference.NewPath;
            reference.Folder.DisplayName = reference.NewDisplayName;
        }

        foreach (var config in references.Select(reference => reference.Config).Distinct())
        {
            string oldPath = references.First(reference => ReferenceEquals(reference.Config, config)).OldPath;
            string newPath = references.First(reference => ReferenceEquals(reference.Config, config)).NewPath;
            if (config.Automation != null
                && AreSamePath(config.Automation.TargetFolderPath, oldPath))
            {
                config.Automation.TargetFolderPath = newPath;
            }
        }
    }

    private static void UpdateGlobalPathReferences(
        GlobalSettings? settings,
        string oldPath,
        string newPath)
    {
        if (settings == null)
        {
            return;
        }

        if (AreSamePath(settings.LastManagerFolderPath, oldPath))
        {
            settings.LastManagerFolderPath = newPath;
        }

        if (AreSamePath(settings.LastHistoryFolderPath, oldPath))
        {
            settings.LastHistoryFolderPath = newPath;
        }
    }

    private static RenameRuntimeState CaptureRuntimeState(
        IReadOnlyList<FolderRenameReferencePlan> references,
        string oldPath)
    {
        bool miniWindowOpen = GetPathCandidates(oldPath).Any(MiniWindowService.IsOpen);
        bool watcherRunning = GetPathCandidates(oldPath).Any(FolderWatcherService.IsWatching);
        bool watcherHadChanges = GetPathCandidates(oldPath).Any(FolderWatcherService.HasChanges);
        return new RenameRuntimeState(
            references[0].Config,
            references[0].Folder,
            miniWindowOpen,
            watcherRunning,
            watcherHadChanges);
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

    private static void RestoreRuntimeState(RenameRuntimeState state)
    {
        if (state.MiniWindowOpen)
        {
            MiniWindowService.Open(state.Config, state.Folder);
        }
        else if (state.WatcherRunning)
        {
            FolderWatcherService.StartWatching(state.Folder.Path, state.WatcherHadChanges);
        }

        if (state.WatcherHadChanges)
        {
            FolderWatcherService.MarkChanged(state.Folder.Path);
        }
    }

    private static IReadOnlyList<string> RollbackMoves(Stack<FolderMoveOperation> completed)
    {
        var errors = new List<string>();
        while (completed.Count > 0)
        {
            var operation = completed.Pop();
            try
            {
                if (!Directory.Exists(operation.DestinationPath))
                {
                    errors.Add($"Rollback source is missing: {operation.DestinationPath}");
                    continue;
                }

                if (Directory.Exists(operation.SourcePath) || File.Exists(operation.SourcePath))
                {
                    errors.Add($"Rollback destination is occupied: {operation.SourcePath}");
                    continue;
                }

                Directory.Move(operation.DestinationPath, operation.SourcePath);
            }
            catch (Exception ex)
            {
                errors.Add($"{operation.DestinationPath} -> {operation.SourcePath}: {ex.Message}");
            }
        }

        return errors;
    }

    private static bool TryResolveMoveRoot(
        string destinationPath,
        string? childDirectory,
        out string rootPath)
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

            return BackupStoragePathService.TryBuildPathWithinRoot(
                destinationPath,
                childDirectory,
                out rootPath);
        }
        catch
        {
            return false;
        }
    }

    private static FolderRenamePreview InvalidPreview(
        string message,
        string oldPath,
        string oldLeaf,
        string newLeaf)
        => new()
        {
            IsValid = false,
            Message = message,
            OldPath = oldPath,
            OldLeafName = oldLeaf,
            NewLeafName = newLeaf
        };

    private static FolderRenameResult Failed(
        FolderRenamePreview preview,
        string message,
        int? affectedConfigCount = null,
        IReadOnlyList<string>? conflicts = null)
        => new()
        {
            Success = false,
            Message = message,
            OldPath = preview.OldPath,
            NewPath = preview.NewPath,
            AffectedConfigCount = affectedConfigCount ?? preview.AffectedConfigCount,
            AffectedHistoryCount = preview.AffectedHistoryCount,
            Conflicts = conflicts ?? Array.Empty<string>()
        };

    private static bool IsInvalidWindowsLeafName(
        string rawLeafName,
        string normalizedLeafName)
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

        return WindowsReservedDeviceNames.Contains(
            reservedCandidate,
            StringComparer.OrdinalIgnoreCase);
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

    private static bool TryResolveRenameablePath(
        string path,
        out string oldLeaf,
        out string parent)
    {
        oldLeaf = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFileName(path);
        parent = Path.GetDirectoryName(path) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(oldLeaf)
            && !string.IsNullOrWhiteSpace(parent)
            && !AreSamePath(path, parent);
    }

    private static bool IsStrictDescendant(string candidate, string root)
        => !AreSamePath(candidate, root)
            && BackupStoragePathService.IsPathInsideRoot(candidate, root);

    private static bool AreSamePath(string? left, string? right)
    {
        string normalizedLeft = NormalizePathForComparison(left);
        string normalizedRight = NormalizePathForComparison(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(
                normalizedLeft,
                normalizedRight,
                StringComparison.OrdinalIgnoreCase);
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
            && (trimmed.EndsWith(Path.DirectorySeparatorChar)
                || trimmed.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    private sealed record RenameRuntimeState(
        BackupConfig Config,
        ManagedFolder Folder,
        bool MiniWindowOpen,
        bool WatcherRunning,
        bool WatcherHadChanges);

    private sealed class PathPairComparer
        : IEqualityComparer<(string Source, string Destination)>
    {
        public static PathPairComparer Instance { get; } = new();

        public bool Equals(
            (string Source, string Destination) left,
            (string Source, string Destination) right)
            => string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    left.Destination,
                    right.Destination,
                    StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Source, string Destination) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Destination));
    }
}
