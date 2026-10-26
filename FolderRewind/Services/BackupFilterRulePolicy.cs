using System;
using System.Collections.Generic;
using System.Linq;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services
{
    internal static class BackupFilterRulePolicy
    {
        public static void AddDistinct(ICollection<PathFilterRule> rules, PathFilterRule rule)
        {
            var normalized = rule with { Pattern = rule.Pattern.Trim() };
            if (!string.IsNullOrWhiteSpace(normalized.Pattern) && !rules.Contains(normalized)) rules.Add(normalized);
        }
        public static void AddDistinct(ICollection<PathFilterRule> rules, string rule) => AddDistinct(rules, PathFilterRule.Legacy(rule));
        public static void AddDistinct(ICollection<string> rules, string rule)
        {
            var trimmed = rule.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            if (rules.Any(existing => string.Equals(existing?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            rules.Add(trimmed);
        }
    }
}
