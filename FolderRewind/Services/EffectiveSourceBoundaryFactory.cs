using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

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
