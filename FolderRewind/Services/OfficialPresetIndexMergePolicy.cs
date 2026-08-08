using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal static class OfficialPresetIndexMergePolicy
{
    public static IReadOnlyList<T> MergeV2First<T>(
        IEnumerable<T>? v2Items,
        IEnumerable<T>? legacyItems,
        Func<T, string?> identitySelector)
    {
        ArgumentNullException.ThrowIfNull(identitySelector);
        var result = new List<T>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (v2Items ?? Array.Empty<T>()).Concat(legacyItems ?? Array.Empty<T>()))
        {
            var identity = identitySelector(item)?.Trim() ?? string.Empty;
            if (identity.Length == 0 || identities.Add(identity))
            {
                result.Add(item);
            }
        }
        return result;
    }
}
