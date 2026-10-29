using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
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
        /// <summary>
        /// 枚举源目录内全部受保护文件（权威枚举路径）：跳过 System 属性与重解析点，
        /// Include 源范围先于任何配置过滤器判定（下游规则只能收窄边界），再应用附加过滤器。
        /// 结果按相对路径排序；元数据直接来自同次目录枚举。
        /// </summary>
        public static IReadOnlyList<BackupSourceFile> Enumerate(
        string sourceRoot,
        BackupSourceScope? sourceScope,
        Func<string, bool>? additionalFilter = null,
        CancellationToken cancellationToken = default)
        => EnumerateUnordered(sourceRoot, sourceScope, additionalFilter, cancellationToken)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

    // Capture builds a dictionary and does not need an intermediate sorted list.
    internal static IEnumerable<BackupSourceFile> EnumerateUnordered(
        string sourceRoot, BackupSourceScope? sourceScope,
        Func<string, bool>? additionalFilter = null, CancellationToken cancellationToken = default, bool requireComplete = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(sourceRoot);
        if (requireComplete && (File.GetAttributes(root) & FileAttributes.Directory) == 0)
            throw new IOException("Capture source is not a directory: " + root);
        if (!Directory.Exists(root) || PreviewStoragePaths.IsReserved(root))
        {
            yield break;
        }

        sourceScope ??= new BackupSourceScope();
        var includePatterns = sourceScope.Mode == BackupSourceScopeMode.Include
            ? BackupSourceScopePatternSet.Compile(sourceScope.IncludePatterns)
            : null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = !requireComplete,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var files = new FileSystemEnumerable<BackupSourceFile?>(root, (ref FileSystemEntry entry) =>
        {
            try
            {
                var path = entry.ToFullPath();
                return new BackupSourceFile
                {
                    FullPath = path,
                    RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Size = entry.Length,
                    LastWriteTimeUtc = entry.LastWriteTimeUtc.UtcDateTime
                };
            }
            catch (IOException) when (!requireComplete) { return null; }
            catch (UnauthorizedAccessException) when (!requireComplete) { return null; }
        }, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.FileName.Equals(PreviewStoragePaths.DirectoryName.AsSpan(), StringComparison.OrdinalIgnoreCase)
        };
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null) continue;
            var relativePath = file.RelativePath;
            if (!IsSafeRelativeFilePath(relativePath) || PreviewStoragePaths.IsReserved(relativePath))
            {
                continue;
            }
            if (sourceScope.Mode == BackupSourceScopeMode.Include
                && includePatterns?.IsMatch(relativePath) != true)
            {
                continue;
            }
            if (additionalFilter != null && !additionalFilter(file.FullPath))
            {
                continue;
            }

            yield return file;
        }
    }

        /// <summary>
        /// 校验并规范化 Include 源范围的模式列表；All 模式直接通过，未知模式抛出异常。
        /// </summary>
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

        /// <summary>
        /// 判断相对路径是否安全：非空、无换行、非根路径、不含 ".." 路径段（防路径穿越）。
        /// </summary>
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
