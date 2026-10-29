using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Retains whitelist paths absent from a materialized archive without masking archive content.</summary>
internal static class RestoreWhitelistPreparation
{
    public static async Task<IReadOnlyList<RestoreStagedFileProposal>> PrepareAsync(IRestoreSourceView current,
        IRestoreSourceView target, IEnumerable<PathFilterRule> rules, string comparisonRoot, CancellationToken token, bool useRegex = false)
    {
        var activeRules = rules.Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern)).ToArray();
        if (activeRules.Length == 0) return [];
        var matcher = PathRuleMatcher.CreateForRestoreRules(activeRules, comparisonRoot, useRegex);
        var targetPaths = new HashSet<string>(target.RelativePaths, StringComparer.OrdinalIgnoreCase);
        var proposals = new List<RestoreStagedFileProposal>();
        long bytes = 0;
        foreach (var relative in current.RelativePaths)
        {
            token.ThrowIfCancellationRequested();
            if (targetPaths.Contains(relative) || !matcher.IsMatch(Path.Combine(comparisonRoot, relative))) continue;
            await using var input = await current.OpenReadAsync(relative, token).ConfigureAwait(false);
            bytes = checked(bytes + input.Length);
            if (bytes > 64 * 1024 * 1024 || proposals.Count >= 4096)
                throw new InvalidDataException("Restore whitelist exceeds the preparation limit.");
            using var buffer = new MemoryStream(); await input.CopyToAsync(buffer, token).ConfigureAwait(false);
            proposals.Add(new(relative, buffer.ToArray()));
        }
        return proposals.AsReadOnly();
    }
}
