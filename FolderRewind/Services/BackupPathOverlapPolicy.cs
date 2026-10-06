using System;
using System.Collections.Generic;
using System.IO;

namespace FolderRewind.Services
{
    public enum BackupPathOverlapKind
    {
        None,
        SamePath,
        TargetInsideSource,
        SourceInsideTarget,
        InvalidPath
    }

    public sealed record BackupPathOverlapResult(
        BackupPathOverlapKind Kind,
        string SourcePath,
        string TargetPath,
        string ErrorMessage)
    {
        public bool IsSafe => Kind == BackupPathOverlapKind.None;
    }

    /// <summary>
    /// Enforces the invariant that a backup source and every directory used to store its
    /// archives or metadata are disjoint. Both lexical paths and resolvable link targets
    /// are compared so callers cannot bypass the invariant with relative segments,
    /// symlinks, or Windows junctions.
    /// </summary>
    public static class BackupPathOverlapPolicy
    {
        public static BackupPathOverlapResult Validate(string? sourcePath, params string?[] targetPaths)
        {
            if (!TryNormalize(sourcePath, resolveLinks: false, out var lexicalSource)
                || !TryNormalize(sourcePath, resolveLinks: true, out var canonicalSource))
            {
                return Invalid(sourcePath, string.Empty);
            }

            foreach (var targetPath in targetPaths ?? Array.Empty<string?>())
            {
                if (!TryNormalize(targetPath, resolveLinks: false, out var lexicalTarget)
                    || !TryNormalize(targetPath, resolveLinks: true, out var canonicalTarget))
                {
                    return Invalid(sourcePath, targetPath);
                }

                var lexical = Classify(lexicalSource, lexicalTarget);
                if (lexical != BackupPathOverlapKind.None)
                {
                    return Create(lexical, lexicalSource, lexicalTarget);
                }

                var canonical = Classify(canonicalSource, canonicalTarget);
                if (canonical != BackupPathOverlapKind.None)
                {
                    return Create(canonical, canonicalSource, canonicalTarget);
                }
            }

            return new BackupPathOverlapResult(
                BackupPathOverlapKind.None,
                lexicalSource,
                string.Empty,
                string.Empty);
        }

        private static BackupPathOverlapResult Invalid(string? sourcePath, string? targetPath) => new(
            BackupPathOverlapKind.InvalidPath,
            sourcePath ?? string.Empty,
            targetPath ?? string.Empty,
            "The source or backup storage path is invalid.");

        private static BackupPathOverlapResult Create(
            BackupPathOverlapKind kind,
            string sourcePath,
            string targetPath)
        {
            var relation = kind switch
            {
                BackupPathOverlapKind.SamePath => "is the same as",
                BackupPathOverlapKind.TargetInsideSource => "contains",
                BackupPathOverlapKind.SourceInsideTarget => "is inside",
                _ => "overlaps"
            };
            return new BackupPathOverlapResult(
                kind,
                sourcePath,
                targetPath,
                $"The backup source '{sourcePath}' {relation} the backup storage path '{targetPath}'.");
        }

        private static BackupPathOverlapKind Classify(string sourcePath, string targetPath)
        {
            if (PathEquals(sourcePath, targetPath))
            {
                return BackupPathOverlapKind.SamePath;
            }
            if (IsDescendant(targetPath, sourcePath))
            {
                return BackupPathOverlapKind.TargetInsideSource;
            }
            if (IsDescendant(sourcePath, targetPath))
            {
                return BackupPathOverlapKind.SourceInsideTarget;
            }
            return BackupPathOverlapKind.None;
        }

        private static bool IsDescendant(string candidate, string parent)
        {
            if (!candidate.StartsWith(parent, PathComparison))
            {
                return false;
            }
            if (candidate.Length <= parent.Length)
            {
                return false;
            }
            if (IsDirectorySeparator(parent[^1]))
            {
                return true;
            }
            return IsDirectorySeparator(candidate[parent.Length]);
        }

        private static bool PathEquals(string left, string right) =>
            string.Equals(left, right, PathComparison);

        private static bool TryNormalize(string? path, bool resolveLinks, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                normalized = resolveLinks ? ResolveLinks(fullPath) : TrimTrailingSeparators(fullPath);
                return !string.IsNullOrWhiteSpace(normalized);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveLinks(string fullPath)
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return TrimTrailingSeparators(fullPath);
            }

            var current = root;
            var relative = fullPath[root.Length..];
            foreach (var segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo? linkTarget = null;
                if (Directory.Exists(current))
                {
                    linkTarget = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                }
                else if (File.Exists(current))
                {
                    linkTarget = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                }

                if (linkTarget != null)
                {
                    current = linkTarget.FullName;
                }
            }

            return TrimTrailingSeparators(Path.GetFullPath(current));
        }

        private static string TrimTrailingSeparators(string path)
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length < root.Length ? root : trimmed;
        }

        private static bool IsDirectorySeparator(char value) =>
            value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
