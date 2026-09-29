using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderRewind.History.Capture;

/// <summary>
/// 源目录的单条权威枚举路径。Include 范围先于任何配置过滤器判定，下游规则只能继续收窄边界、不能扩大。
/// 相对路径统一用 '/' 分隔并以序号序排序，与 Native History 的树摘要保持一致。
/// </summary>
internal static class HistorySourceFileEnumerator
{
    /// <summary>
    /// 枚举源目录内全部受保护文件：跳过 System 属性与重解析点，先按 Include 源范围筛选，
    /// 再应用配置过滤器与附加过滤器。单个文件的信息读取失败（IO/权限）静默跳过。
    /// </summary>
    public static IReadOnlyList<string> Enumerate(
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
            return [];
        }

        sourceScope ??= new BackupSourceScope();
        var includePatterns = sourceScope.Mode == BackupSourceScopeMode.Include
            ? BackupSourceScopePatternSet.Compile(sourceScope.IncludePatterns)
            : null;
        var result = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // 重解析点必须跳过：跟随链接会让同一份内容以两条路径进入历史，树摘要也就不可信了。
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
            if (additionalFilter is not null && !additionalFilter(path))
            {
                continue;
            }

            result.Add(relativePath);
        }

        return result
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 扫描源目录并返回相对路径 → 文件状态表，供捕获比对使用。
    /// 文件大小与写入时间在枚举之后单独读取，读取失败的条目整条丢弃，避免留下无法比对的状态。
    /// </summary>
    public static Dictionary<string, SourceCaptureFileState> Scan(
        string sourceRoot,
        FilterSettings? filters,
        BackupSourceScope? sourceScope,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(sourceRoot);
        var states = new Dictionary<string, SourceCaptureFileState>(StringComparer.Ordinal);
        foreach (var relativePath in Enumerate(
                     root,
                     sourceScope,
                     filters is null
                         ? null
                         : path => BackupService.ShouldIncludeInBackup(path, root, root, filters),
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                states[relativePath] = new SourceCaptureFileState(info.Length, info.LastWriteTimeUtc);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return states;
    }

    /// <summary>
    /// 判断相对路径是否可安全落进历史：非空、无换行、非根路径、不含 ".." 路径段。
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
