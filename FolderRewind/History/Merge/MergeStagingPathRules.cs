using System;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Merge;

/// <summary>
/// Merge 暂存区的路径规则：把相对路径规范化、解析到受控根目录之下，并在落盘前校验单条路径的 Windows 安全性。
/// 复刻自 1.9.x 插件运行时项目的 ArtifactPathRules / RestoreStagingProposalValidator，去掉插件产物语义后并入历史子系统。
/// </summary>
public static class MergeStagingPathRules
{
    /// <summary>把相对路径规范化为以 '/' 分隔的形式；含盘符、绝对路径或非法段时抛出。</summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Merge staging path must be relative and cannot contain an alternate data stream.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new InvalidDataException("Merge staging path contains an invalid segment.");
        }
        return string.Join('/', segments);
    }

    /// <summary>把相对路径解析到 root 之下，并确认结果没有越出 root。</summary>
    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Merge staging path escapes its Host-owned root.");
        }
        return candidate;
    }

    /// <summary>
    /// 校验单条暂存相对路径可用于落盘：必须是相对路径，且各段不含 Windows 非法字符、结尾点/空格或保留设备名。
    /// </summary>
    public static void ValidateStagingRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Merge staging paths must be relative.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Split('/').Any(segment => segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || segment.Any(char.IsControl)
                || IsDeviceName(segment.Split('.')[0])))
        {
            throw new InvalidDataException("Merge staging contains an unsafe Windows path.");
        }
    }

    private static bool IsDeviceName(string name)
        => name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && name[3] is >= '1' and <= '9'
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
}
