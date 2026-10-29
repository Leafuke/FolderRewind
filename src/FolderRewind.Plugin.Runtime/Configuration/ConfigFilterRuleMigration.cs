using System.Text.Json;
using System.Text.Json.Nodes;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Configuration;

internal static class ConfigFilterRuleMigration
{
    internal static bool Upgrade(JsonObject root)
    {
        var changed = !ConfigDocumentValidator.TryGetInt(root, ConfigSchema.VersionPropertyName, out var version) || version != 2;
        root[ConfigSchema.VersionPropertyName] = 2;
        foreach (var name in new[] { "BackupConfigs", "Templates", "BackupPresets" })
        {
            if (ConfigDocumentValidator.Get(root, name) is not JsonArray entries) continue;
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (name != "BackupConfigs" && ConfigDocumentValidator.Get(entry, "SchemaVersion") is not null)
                {
                    changed |= entry["SchemaVersion"]?.GetValue<int>() != 2;
                    entry["SchemaVersion"] = 2;
                }
                if (ConfigDocumentValidator.Get(entry, "Filters") is not JsonObject filters) continue;
                foreach (var list in new[] { "Blacklist", "BackupWhitelist", "RestoreWhitelist" })
                {
                    if (ConfigDocumentValidator.Get(filters, list) is not JsonArray rules) continue;
                    for (var index = 0; index < rules.Count; index++)
                    {
                        if (rules[index] is not JsonValue value || !value.TryGetValue<string>(out var pattern)) continue;
                        rules[index] = JsonSerializer.SerializeToNode(PathFilterRule.Legacy(pattern));
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }
}
