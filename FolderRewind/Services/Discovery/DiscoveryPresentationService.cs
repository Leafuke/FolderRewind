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
        var existing = (existingOrigins ?? Array.Empty<DiscoveryOrigin?>()).FirstOrDefault(origin =>
            origin != null
            && string.Equals(origin.ProviderId, game.Definition.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.DefinitionId, game.Definition.DefinitionId, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return DiscoveryCandidateStatus.New;
        }

        var knownResourceIds = existing.ResourceIds;
        var hasNewResources = game.BackupSets
            .SelectMany(set => set.Resources)
            .Where(CanSelect)
            .Any(resource => !knownResourceIds.Contains(resource.ResourceId, StringComparer.OrdinalIgnoreCase));
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
