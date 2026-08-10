using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public static class DiscoveryCandidateMerger
{
    public static IReadOnlyList<DiscoveredGameCandidate> Merge(
        IEnumerable<DiscoveryProviderResult> providerResults)
    {
        ArgumentNullException.ThrowIfNull(providerResults);

        var merged = new List<DiscoveredGameCandidate>();
        foreach (var candidate in providerResults
                     .Where(result => result != null)
                     .SelectMany(result => result.Candidates ?? Array.Empty<DiscoveredGameCandidate>()))
        {
            var existing = merged.FirstOrDefault(item => IsSameGame(item, candidate));
            if (existing == null)
            {
                merged.Add(CloneCandidate(candidate));
                continue;
            }

            MergeInstallations(existing, candidate);
            MergeBackupSets(existing, candidate);
        }

        foreach (var candidate in merged)
        {
            ApplySpecializedSuppression(candidate);
        }

        return merged
            .OrderBy(candidate => candidate.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsSameGame(DiscoveredGameCandidate left, DiscoveredGameCandidate right)
    {
        if (string.Equals(left.StableKey, right.StableKey, StringComparison.Ordinal))
        {
            return true;
        }

        var leftIds = BuildExternalIdentitySet(left.Definition.ExternalIds);
        var rightIds = BuildExternalIdentitySet(right.Definition.ExternalIds);
        if (leftIds.Overlaps(rightIds))
        {
            return true;
        }

        if (!string.Equals(
                left.Definition.ProviderId,
                right.Definition.ProviderId,
                StringComparison.OrdinalIgnoreCase)
            && HasMergeEvidence(left)
            && HasMergeEvidence(right)
            && (left.Definition.Aliases.Any(alias =>
                NameEquals(alias, right.Definition.DisplayName))
            || right.Definition.Aliases.Any(alias =>
                NameEquals(alias, left.Definition.DisplayName))))
        {
            return true;
        }

        return false;
    }

    private static HashSet<string> BuildExternalIdentitySet(IReadOnlyDictionary<string, string> ids)
    {
        return ids
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .SelectMany(pair => pair.Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => $"{pair.Key.Trim().ToLowerInvariant()}:{value.ToLowerInvariant()}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasMergeEvidence(DiscoveredGameCandidate candidate) =>
        candidate.Installations.Any(installation => installation.Evidence.Any(evidence =>
            evidence.Confidence >= DiscoveryConfidence.Medium))
        || candidate.BackupSets.SelectMany(set => set.Resources).Any(resource =>
            resource.Confidence >= DiscoveryConfidence.Medium);

    private static bool NameEquals(string left, string right) =>
        string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static DiscoveredGameCandidate CloneCandidate(DiscoveredGameCandidate source)
    {
        return new DiscoveredGameCandidate
        {
            StableKey = source.StableKey,
            Definition = source.Definition,
            Installations = source.Installations.ToList(),
            BackupSets = source.BackupSets.Select(CloneBackupSet).ToList()
        };
    }

    private static BackupSetCandidate CloneBackupSet(BackupSetCandidate source)
    {
        return new BackupSetCandidate
        {
            StableKey = source.StableKey,
            Identity = source.Identity,
            DisplayName = source.DisplayName,
            SuggestedConfigType = source.SuggestedConfigType,
            Resources = source.Resources.ToList()
        };
    }

    private static void MergeInstallations(
        DiscoveredGameCandidate target,
        DiscoveredGameCandidate incoming)
    {
        foreach (var installation in incoming.Installations)
        {
            if (target.Installations.Any(existing =>
                    string.Equals(existing.InstallationId, installation.InstallationId, StringComparison.OrdinalIgnoreCase)
                    || (existing.Store == installation.Store
                        && string.Equals(existing.StoreGameId, installation.StoreGameId, StringComparison.OrdinalIgnoreCase)
                        && PathEquals(existing.BasePath, installation.BasePath))))
            {
                continue;
            }

            target.Installations.Add(installation);
        }
    }

    private static void MergeBackupSets(
        DiscoveredGameCandidate target,
        DiscoveredGameCandidate incoming)
    {
        foreach (var backupSet in incoming.BackupSets)
        {
            var existing = target.BackupSets.FirstOrDefault(item =>
                item.Identity.HasSameStableIdentity(backupSet.Identity));
            if (existing == null)
            {
                target.BackupSets.Add(CloneBackupSet(backupSet));
                continue;
            }

            foreach (var resource in backupSet.Resources)
            {
                if (!existing.Resources.Any(item =>
                        string.Equals(item.ResourceId, resource.ResourceId, StringComparison.OrdinalIgnoreCase)))
                {
                    existing.Resources.Add(resource);
                }
            }
        }
    }

    private static void ApplySpecializedSuppression(DiscoveredGameCandidate candidate)
    {
        var resources = candidate.BackupSets
            .SelectMany(set => set.Resources)
            .OrderByDescending(resource => resource.IsSpecializedProvider)
            .ThenByDescending(resource => resource.ProviderPriority)
            .ToList();
        for (var index = 0; index < resources.Count; index++)
        {
            var winner = resources[index];
            if (!winner.IsSpecializedProvider || winner.IsSuppressed)
            {
                continue;
            }

            for (var otherIndex = index + 1; otherIndex < resources.Count; otherIndex++)
            {
                var other = resources[otherIndex];
                if (other.IsSpecializedProvider || other.IsSuppressed)
                {
                    continue;
                }

                if (ResourcesEquivalent(winner, other))
                {
                    other.SuppressedByProviderId = winner.ProviderId;
                    other.SuppressionReason = $"Has the same effective source scope as specialized resource {winner.ResourceId}.";
                }
                else if (ResourcesMayOverlap(winner, other))
                {
                    var warning = $"Partially overlaps resource '{winner.ResourceId}' from specialized provider '{winner.ProviderId}'.";
                    other.ConflictWarning = warning;
                    winner.ConflictWarning = $"Partially overlaps generic resource '{other.ResourceId}'.";
                }
            }
        }
    }

    private static bool ResourcesEquivalent(BackupResourceCandidate left, BackupResourceCandidate right)
    {
        if (left.Kind == BackupResourceKind.Registry || right.Kind == BackupResourceKind.Registry)
        {
            return left.Kind == right.Kind
                   && string.Equals(left.OriginalExpression, right.OriginalExpression, StringComparison.OrdinalIgnoreCase);
        }

        if (!PathEquals(left.FixedRoot, right.FixedRoot))
        {
            return false;
        }

        return left.IncludePatterns.Count == right.IncludePatterns.Count
               && left.IncludePatterns.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(right.IncludePatterns);
    }

    private static bool ResourcesMayOverlap(BackupResourceCandidate left, BackupResourceCandidate right)
    {
        if (left.Kind == BackupResourceKind.Registry || right.Kind == BackupResourceKind.Registry)
        {
            return false;
        }

        var leftRoot = NormalizePath(left.FixedRoot);
        var rightRoot = NormalizePath(right.FixedRoot);
        if (PathEquals(leftRoot, rightRoot))
        {
            return left.IncludePatterns.Count == 0
                   || right.IncludePatterns.Count == 0
                   || left.IncludePatterns.Intersect(right.IncludePatterns, StringComparer.OrdinalIgnoreCase).Any();
        }

        return IsDescendant(leftRoot, rightRoot) || IsDescendant(rightRoot, leftRoot);
    }

    private static bool IsDescendant(string candidate, string parent) =>
        candidate.Length > parent.Length
        && candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && (parent.EndsWith(Path.DirectorySeparatorChar)
            || candidate[parent.Length] is '\\' or '/');

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim().TrimEnd('\\', '/');
        }
    }
}
