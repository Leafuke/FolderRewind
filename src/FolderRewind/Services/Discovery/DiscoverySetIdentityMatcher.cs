using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public static class DiscoverySetIdentityMatcher
{
    public static T? FindUnique<T>(
        DiscoverySetIdentity identity,
        IEnumerable<T>? candidates,
        Func<T, DiscoveryOrigin?> originSelector)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(originSelector);
        var materialized = (candidates ?? Array.Empty<T>())
            .Where(candidate => originSelector(candidate)?.Identity != null)
            .ToList();
        var exact = materialized.Where(candidate =>
                originSelector(candidate)!.Identity.HasSameStableIdentity(identity))
            .ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }
        if (exact.Count > 1)
        {
            return null;
        }

        var externalKeys = identity.ExternalIdentityKeys();
        if (externalKeys.Count == 0)
        {
            return null;
        }
        var external = materialized.Where(candidate =>
                string.Equals(
                    originSelector(candidate)!.Identity.ProviderId,
                    identity.ProviderId,
                    StringComparison.OrdinalIgnoreCase)
                && originSelector(candidate)!.Identity.ExternalIdentityKeys().Overlaps(externalKeys))
            .ToList();
        return external.Count == 1 ? external[0] : null;
    }
}
