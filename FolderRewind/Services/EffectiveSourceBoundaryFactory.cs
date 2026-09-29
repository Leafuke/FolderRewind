using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>
/// 把「源目录 + 文件夹范围 + 配置过滤器」折叠成捕获时用来界定受管理文件集合的不可变快照。
/// 这是边界解析的核心层，不依赖插件；插件贡献在调用方折叠进来之后再走这里。
/// </summary>
internal static class EffectiveSourceBoundaryFactory
{
    public static EffectiveSourceBoundarySnapshot Create(
        string sourceRoot,
        BackupSourceScope? sourceScope,
        FilterSettings? filters)
    {
        sourceScope ??= new BackupSourceScope();
        var scopeRules = sourceScope.Mode == BackupSourceScopeMode.Include
            ? BackupSourceScopePatternSet.NormalizeAndValidate(sourceScope.IncludePatterns)
                .Select(NormalizeCaseInsensitiveRule)
            : [];
        var filterMode = filters?.BackupFilterMode == BackupFilterMode.Whitelist
            ? EffectiveBoundaryFilterMode.Whitelist
            : EffectiveBoundaryFilterMode.Blacklist;
        var rawFilterRules = filterMode == EffectiveBoundaryFilterMode.Whitelist
            ? filters?.BackupWhitelist
            : filters?.Blacklist;
        PathRuleMatcher.ValidateBackupRules(rawFilterRules, filters?.UseRegex == true);
        var filterRules = NormalizeFilterRules(sourceRoot, rawFilterRules);

        return new EffectiveSourceBoundarySnapshot(
            sourceScope.Mode == BackupSourceScopeMode.Include
                ? EffectiveBoundaryScopeMode.Include
                : EffectiveBoundaryScopeMode.All,
            scopeRules,
            filterMode,
            filterRules,
            filters?.UseRegex == true);
    }

    /// <summary>
    /// 过滤规则统一折算成相对源根、以 '/' 分隔、大小写无关的形式，保证同一个逻辑边界在不同设备上得到同一个指纹。
    /// </summary>
    private static IEnumerable<string> NormalizeFilterRules(
        string sourceRoot,
        IEnumerable<string>? rules)
    {
        var root = Path.GetFullPath(sourceRoot);
        foreach (var raw in rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                yield return "regex:" + trimmed[6..];
                continue;
            }

            if (Path.IsPathRooted(trimmed))
            {
                var relative = Path.GetRelativePath(root, Path.GetFullPath(trimmed));
                if (!Path.IsPathRooted(relative)
                    && !relative.Equals("..", StringComparison.Ordinal)
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    trimmed = relative;
                }
            }

            yield return NormalizeCaseInsensitiveRule(trimmed);
        }
    }

    private static string NormalizeCaseInsensitiveRule(string value)
        => value.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
}
