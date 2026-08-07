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
        if (string.Equals(left.StableKey, right.StableKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftIds = BuildExternalIdentitySet(left.Definition.ExternalIds);
        var rightIds = BuildExternalIdentitySet(right.Definition.ExternalIds);
        if (leftIds.Overlaps(rightIds))
        {
            return true;
        }

        if (left.Definition.Aliases.Any(alias =>
                NameEquals(alias, right.Definition.DisplayName))
            || right.Definition.Aliases.Any(alias =>
                NameEquals(alias, left.Definition.DisplayName)))
        {
            return true;
        }

        return false;
    }

    private static HashSet<string> BuildExternalIdentitySet(IReadOnlyDictionary<string, string> ids)
    {
        return ids
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key.Trim().ToLowerInvariant()}:{pair.Value.Trim().ToLowerInvariant()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

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
                        && PathEquals(existing.InstallPath, installation.InstallPath))))
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
                string.Equals(item.StableKey, backupSet.StableKey, StringComparison.OrdinalIgnoreCase));
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
        foreach (var backupSet in candidate.BackupSets)
        {
            var resources = backupSet.Resources
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
                    if (other.IsSpecializedProvider || other.IsSuppressed || !ResourcesOverlap(winner, other))
                    {
                        continue;
                    }

                    other.SuppressedByProviderId = winner.ProviderId;
                    other.SuppressionReason = $"Overlaps specialized provider resource {winner.ResourceId}.";
                }
            }
        }
    }

    private static bool ResourcesOverlap(BackupResourceCandidate left, BackupResourceCandidate right)
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

        if (left.IncludePatterns.Count == 0 || right.IncludePatterns.Count == 0)
        {
            return true;
        }

        return left.IncludePatterns.Intersect(right.IncludePatterns, StringComparer.OrdinalIgnoreCase).Any();
    }

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
