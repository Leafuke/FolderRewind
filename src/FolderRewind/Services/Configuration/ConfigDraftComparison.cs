using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace FolderRewind.Services;

internal static class ConfigDraftComparison
{
    internal static string[] ChangedSections(string expected, string current)
    {
        var left = JsonNode.Parse(expected)!.AsObject();
        var right = JsonNode.Parse(current)!.AsObject();
        return left.Select(p => p.Key).Union(right.Select(p => p.Key), StringComparer.Ordinal)
            .Where(key => !JsonNode.DeepEquals(left[key], right[key])).ToArray();
    }
    internal static bool OnlyRuntimeFactsChanged(string expected, string current)
    {
        var left = JsonNode.Parse(expected)!;
        var right = JsonNode.Parse(current)!;
        RemoveRuntime(left); RemoveRuntime(right);
        return JsonNode.DeepEquals(left, right);
    }
    private static void RemoveRuntime(JsonNode value)
    {
        if (value["SourceFolders"] is JsonArray folders)
            foreach (var folder in folders.OfType<JsonObject>()) folder.Remove("LastBackupTime");
        if (value["Cloud"] is JsonObject cloud)
            foreach (var key in new[] { "LastRunUtc", "LastExitCode", "LastErrorMessage" }) cloud.Remove(key);
    }
}
