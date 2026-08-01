using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services
{
    internal static class BackupFilterRulePolicy
    {
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
