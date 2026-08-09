using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

public sealed class BackupSourceFile
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
}

/// <summary>
/// A source's single authoritative file enumeration path. Selection is evaluated before
/// any configurable filter so downstream rules can never expand the discovered boundary.
/// </summary>
public static class BackupSourceFileEnumerator
{
    public static IReadOnlyList<BackupSourceFile> Enumerate(
        string sourceRoot,
        BackupSourceScope? sourceScope,
        Func<string, bool>? additionalFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
        {
            return Array.Empty<BackupSourceFile>();
        }

        sourceScope ??= new BackupSourceScope();
        var includePatterns = sourceScope.Mode == BackupSourceScopeMode.Include
            ? BackupSourceScopePatternSet.Compile(sourceScope.IncludePatterns)
            : null;
        var result = new List<BackupSourceFile>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!IsSafeRelativeFilePath(relativePath))
            {
                continue;
            }
            if (sourceScope.Mode == BackupSourceScopeMode.Include
                && includePatterns?.IsMatch(relativePath) != true)
            {
                continue;
            }
            if (additionalFilter != null && !additionalFilter(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                result.Add(new BackupSourceFile
                {
                    FullPath = info.FullName,
                    RelativePath = relativePath,
                    Size = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> ValidateAndNormalize(BackupSourceScope sourceScope)
    {
        ArgumentNullException.ThrowIfNull(sourceScope);
        if (sourceScope.Mode == BackupSourceScopeMode.All)
        {
            return Array.Empty<string>();
        }
        if (sourceScope.Mode != BackupSourceScopeMode.Include)
        {
            throw new InvalidDataException($"Unsupported backup source scope mode: {sourceScope.Mode}.");
        }

        return BackupSourceScopePatternSet.NormalizeAndValidate(sourceScope.IncludePatterns);
    }

    public static bool IsSafeRelativeFilePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains('\r')
            || relativePath.Contains('\n')
            || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return !relativePath.Replace('\\', '/').Split('/').Any(segment => segment == "..");
    }
}
