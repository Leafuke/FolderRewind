using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FolderRewind.Plugin.Abstractions;

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
        PathRuleMatcher.ValidateRules(rawFilterRules);
        var filterRules = NormalizeRules(sourceRoot, rawFilterRules);

        return EffectiveSourceBoundarySnapshot.CreateTyped(
            sourceScope.Mode == BackupSourceScopeMode.Include
                ? EffectiveBoundaryScopeMode.Include
                : EffectiveBoundaryScopeMode.All,
            scopeRules,
            filterMode,
            filterRules,
            filters?.UseRegex == true);
    }

    private static IEnumerable<PathFilterRule> NormalizeRules(string root, IEnumerable<PathFilterRule>? rules)
    {
        foreach (var rule in rules ?? [])
        {
            if (rule.SemanticsVersion == 1)
            {
                foreach (var pattern in NormalizeFilterRules(root, [rule.Pattern])) yield return rule with { Pattern = pattern };
                continue;
            }
            var parsed = PathFilterPattern.Parse(rule, root);
            if (!parsed.Applicable) continue;
            yield return parsed.ToBoundaryRule();
        }
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
