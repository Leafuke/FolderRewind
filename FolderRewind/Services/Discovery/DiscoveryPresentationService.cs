using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public enum DiscoveryCandidateStatus
{
    New = 0,
    UpToDate = 1,
    NewResources = 2
}

public static class DiscoveryPresentationService
{
    public static bool CanSelect(BackupResourceCandidate resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SupportState == BackupResourceSupportState.Supported && !resource.IsSuppressed;
    }

    public static bool IsSelectedByDefault(BackupResourceCandidate resource)
    {
        return CanSelect(resource) && resource.IsSelectedByDefault;
    }

    public static DiscoveryCandidateStatus GetStatus(
        DiscoveredGameCandidate game,
        IEnumerable<DiscoveryOrigin?>? existingOrigins)
    {
        ArgumentNullException.ThrowIfNull(game);
        var origins = (existingOrigins ?? Array.Empty<DiscoveryOrigin?>())
            .Where(origin => origin?.Identity != null)
            .Cast<DiscoveryOrigin>()
            .ToList();
        var matchedCount = 0;
        var hasNewResources = false;
        foreach (var set in game.BackupSets)
        {
            var existing = DiscoverySetIdentityMatcher.FindUnique(
                set.Identity,
                origins,
                origin => origin);
            if (existing == null)
            {
                hasNewResources = true;
                continue;
            }

            matchedCount++;
            var knownResourceIds = existing.ReviewedBaseline.Sources
                .SelectMany(source => source.ResourceIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (set.Resources
                .Where(CanSelect)
                .Any(resource => !knownResourceIds.Contains(resource.ResourceId)))
            {
                hasNewResources = true;
            }
        }

        if (matchedCount == 0)
        {
            return DiscoveryCandidateStatus.New;
        }
        return hasNewResources
            ? DiscoveryCandidateStatus.NewResources
            : DiscoveryCandidateStatus.UpToDate;
    }

    public static bool Matches(
        DiscoveredGameCandidate game,
        DiscoveryCandidateStatus status,
        string? searchText,
        GameStore? store,
        DiscoveryCandidateStatus? statusFilter)
    {
        ArgumentNullException.ThrowIfNull(game);
        var search = searchText?.Trim() ?? string.Empty;
        if (search.Length > 0
            && !game.Definition.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            && !game.Definition.Aliases.Any(alias => alias.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            return false;
        }
        if (store.HasValue && game.Installations.All(installation => installation.Store != store.Value))
        {
            return false;
        }
        return !statusFilter.HasValue || status == statusFilter.Value;
    }
}
