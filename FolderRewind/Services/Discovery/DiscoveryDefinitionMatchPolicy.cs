using System;

namespace FolderRewind.Services.Discovery;

internal static class DiscoveryDefinitionMatchPolicy
{
    public static bool Matches(
        string? sourceProviderId,
        string? sourceDefinitionId,
        string? providerId,
        string? definitionId) =>
        !string.IsNullOrWhiteSpace(sourceProviderId)
        && !string.IsNullOrWhiteSpace(sourceDefinitionId)
        && string.Equals(sourceProviderId, providerId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(sourceDefinitionId, definitionId, StringComparison.Ordinal);
}
