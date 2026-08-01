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

        return ExecuteMovePlanCore(operations, cancellationToken).Result;
    }

    private static MoveExecutionResult ExecuteMovePlanCore(
        IReadOnlyList<FolderMoveOperation> operations,
        CancellationToken cancellationToken)
    {
        var completed = new List<FolderMoveOperation>();

        try
        {
            foreach (var operation in operations ?? Array.Empty<FolderMoveOperation>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(operation.SourcePath, operation.DestinationPath);
                completed.Add(operation);
            }

            return new MoveExecutionResult(
                new FolderRenameResult
                {
                    Success = true
                },
                completed);
        }
        catch (Exception ex)
        {
            var rollbackErrors = RollbackMoves(completed);
            return new MoveExecutionResult(
                new FolderRenameResult
                {
                    Success = false,
                    Message = ex.Message,
                    RollbackSucceeded = rollbackErrors.Count == 0,
                    RollbackErrors = rollbackErrors
                },
                Array.Empty<FolderMoveOperation>());
        }
    }

    public static IReadOnlyList<string> ValidateMovePlan(IReadOnlyList<FolderMoveOperation> operations)
    {
        var conflicts = new List<string>();
        var normalized = new List<(string Source, string Destination)>();

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

            normalized.Add((source, destination));
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
