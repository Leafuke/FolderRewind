using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services;

/// <summary>Only unchanged local source identity and semantics justify retaining a baseline.</summary>
internal static class MigrationRecoveryPolicy
{
    internal static string[] ResetSources(JsonElement current, JsonElement? previous, bool localRestore)
    {
        var sources = current.GetProperty("SourceFolders").EnumerateArray().ToArray();
        if (!localRestore || previous is null) return sources.Select(s => s.GetProperty("Id").GetString()!).ToArray();
        var oldSources = previous.Value.GetProperty("SourceFolders").EnumerateArray().ToArray();
        var sameSemantics = new[] { "Kind", "RequiredPluginId", "BackupScope", "Filters", "ProviderStates", "ConsistencyIntent", "ArtifactTransformPolicy" }
            .All(key => EqualField(current, previous.Value, key));
        return sources.Where(source => !sameSemantics || !oldSources.Any(old =>
            SameId(source.GetProperty("Id").GetString()!, old.GetProperty("Id").GetString()!)
            && StringComparer.OrdinalIgnoreCase.Equals(source.GetProperty("Path").GetString(), old.GetProperty("Path").GetString())
            && EqualField(source, old, "SourceScope")))
            .Select(s => s.GetProperty("Id").GetString()!).ToArray();
    }
    internal static bool SameId(string a, string b) => Guid.TryParse(a, out var first) && Guid.TryParse(b, out var second)
        ? first == second : StringComparer.Ordinal.Equals(a, b);
    private static bool EqualField(JsonElement a, JsonElement b, string key)
    {
        var hasA = a.TryGetProperty(key, out var first); var hasB = b.TryGetProperty(key, out var second);
        return hasA == hasB && (!hasA || JsonElement.DeepEquals(first, second));
    }
}
