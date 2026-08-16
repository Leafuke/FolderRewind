using System.Text.Json.Nodes;

namespace FolderRewind.Plugin.Runtime.Configuration;

public sealed record LegacyConfigMigrationResult(JsonObject Document, IReadOnlyList<string> Warnings);

public sealed class LegacyConfigMigrator
{
    private static readonly string[] MineRewindBooleanSettings =
    [
        "AutoDiscoverSaves",
        "AutoCreateConfigs",
        "PreservePlayerData"
    ];

    private readonly Func<Guid> _newGuid;

    public LegacyConfigMigrator(Func<Guid>? newGuid = null)
    {
        _newGuid = newGuid ?? Guid.NewGuid;
    }

    public LegacyConfigMigrationResult Migrate(JsonObject legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        if (ConfigDocumentValidator.TryGetInt(legacy, ConfigSchema.VersionPropertyName, out var version)
            && version == ConfigSchema.CurrentVersion)
        {
            return new LegacyConfigMigrationResult((JsonObject)legacy.DeepClone(), Array.Empty<string>());
        }

        var root = (JsonObject)legacy.DeepClone();
        root[ConfigSchema.VersionPropertyName] = ConfigSchema.CurrentVersion;
        var warnings = new List<string>();
        var usedConfigIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedFolderIds = new HashSet<Guid>();

        var globalSettings = EnsureObject(root, "GlobalSettings");
        MigratePluginSettings(globalSettings, warnings);

        var configs = EnsureArray(root, "BackupConfigs");
        for (var configIndex = 0; configIndex < configs.Count; configIndex++)
        {
            if (configs[configIndex] is not JsonObject config)
            {
                warnings.Add($"BackupConfigs[{configIndex}] was not an object and could not be migrated.");
                continue;
            }

            EnsureConfigId(config, usedConfigIds);
            MigrateConfig(config, configIndex, usedFolderIds, warnings);
        }

        MigratePresets(root, warnings);

        return new LegacyConfigMigrationResult(root, warnings);
    }

    private static void MigratePresets(JsonObject root, List<string> warnings)
    {
        if (ConfigDocumentValidator.Get(root, "Templates") is not JsonArray presets)
        {
            return;
        }

        for (var index = 0; index < presets.Count; index++)
        {
            if (presets[index] is not JsonObject preset)
            {
                warnings.Add($"Templates[{index}] was not an object and could not be migrated.");
                continue;
            }

            preset["SchemaVersion"] = ConfigSchema.CurrentVersion;
            var configType = ConfigDocumentValidator.GetString(preset, "BaseConfigType")?.Trim();
            var isMinecraft = string.Equals(configType, "Minecraft Saves", StringComparison.OrdinalIgnoreCase);
            preset["Kind"] = isMinecraft
                ? Kind(ConfigSchema.MineRewindPluginId, ConfigSchema.MineRewindKindId)
                : Kind(ConfigSchema.CoreOwnerId, ConfigSchema.CoreDefaultKindId);
            EnsureObject(preset, "ProviderDefaults");

            if (isMinecraft)
            {
                var required = EnsureArray(preset, "RequiredPluginIds");
                if (!required.Any(node => node is JsonValue value
                                          && value.TryGetValue<string>(out var text)
                                          && string.Equals(text, ConfigSchema.MineRewindPluginId, StringComparison.OrdinalIgnoreCase)))
                {
                    required.Add(ConfigSchema.MineRewindPluginId);
                }
            }

            // clean break 后，已知 v2 字段只参与迁移，不能进入 schema v1 文档。
            RemoveProperty(preset, "BaseConfigType");
        }
    }

    private void MigrateConfig(
        JsonObject config,
        int configIndex,
        HashSet<Guid> usedFolderIds,
        List<string> warnings)
    {
        var configType = ConfigDocumentValidator.GetString(config, "ConfigType")?.Trim() ?? string.Empty;
        var extended = EnsureObject(config, "ExtendedProperties");
        var pluginMarker = ConfigDocumentValidator.GetString(extended, "Plugin")?.Trim() ?? string.Empty;
        var isMinecraft = string.Equals(configType, "Minecraft Saves", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pluginMarker, ConfigSchema.MineRewindPluginId, StringComparison.OrdinalIgnoreCase);
        var isCore = string.IsNullOrWhiteSpace(configType)
            || string.Equals(configType, "Default", StringComparison.OrdinalIgnoreCase);

        if (isMinecraft)
        {
            config["Kind"] = Kind(ConfigSchema.MineRewindPluginId, ConfigSchema.MineRewindKindId);
            config["RequiredPluginId"] = ConfigSchema.MineRewindPluginId;
        }
        else
        {
            config["Kind"] = Kind(ConfigSchema.CoreOwnerId, ConfigSchema.CoreDefaultKindId);
            if (!isCore)
            {
                var warning = $"Unknown legacy ConfigType '{configType}' was preserved and mapped to the Core default kind.";
                warnings.Add(warning);
                AppendLegacyWarning(config, warning);
            }
        }

        var preservation = EnsureObject(config, "LegacyPreservation");
        preservation["OriginalConfigType"] = configType;
        preservation["OriginalExtendedProperties"] = extended.DeepClone();

        if (!string.IsNullOrWhiteSpace(pluginMarker))
        {
            preservation["OriginalPluginMarker"] = pluginMarker;
            config["RequiredPluginId"] = pluginMarker;
        }

        var hostOrigin = new JsonObject();
        CopyStringIfPresent(extended, "TemplateId", hostOrigin);
        CopyStringIfPresent(extended, "TemplateName", hostOrigin);
        if (hostOrigin.Count > 0)
        {
            config["HostOrigin"] = hostOrigin;
        }

        var providerData = new JsonObject();
        CopyStringIfPresent(extended, "MinecraftVersion", providerData);
        CopyStringIfPresent(extended, "MinecraftInstancePath", providerData);
        if (providerData.Count > 0)
        {
            var states = EnsureObject(config, "ProviderStates");
            states[ConfigSchema.MineRewindPluginId] = new JsonObject
            {
                ["SchemaVersion"] = 0,
                ["Data"] = providerData
            };
        }
        else
        {
            EnsureObject(config, "ProviderStates");
        }

        var scope = EnsureObject(config, "BackupScope");
        var legacyScopeId = ConfigDocumentValidator.GetString(scope, "PluginScopeId")?.Trim() ?? string.Empty;
        if (string.Equals(legacyScopeId, "MineRewind.SelectedRegions", StringComparison.OrdinalIgnoreCase))
        {
            scope["OwnerId"] = ConfigSchema.MineRewindPluginId;
            scope["ScopeId"] = ConfigSchema.MineRewindSelectedRegionsScopeId;
        }
        else if (legacyScopeId.Length > 0)
        {
            var warning = $"Unknown legacy backup scope '{legacyScopeId}' was preserved without activation.";
            warnings.Add(warning);
            AppendLegacyWarning(config, warning);
            scope["OwnerId"] = string.Empty;
            scope["ScopeId"] = string.Empty;
        }
        else
        {
            scope["OwnerId"] = string.Empty;
            scope["ScopeId"] = string.Empty;
        }

        EnsureObject(scope, "Parameters");

        var folders = EnsureArray(config, "SourceFolders");
        for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
        {
            if (folders[folderIndex] is not JsonObject folder)
            {
                warnings.Add($"BackupConfigs[{configIndex}].SourceFolders[{folderIndex}] was not an object.");
                continue;
            }

            EnsureFolderId(folder, usedFolderIds);
            EnsureObject(folder, "ProviderStates");
        }

        RemoveProperty(config, "ConfigType");
        RemoveProperty(config, "ExtendedProperties");
        RemoveProperty(scope, "PluginScopeId");
    }

    private static void MigratePluginSettings(JsonObject globalSettings, List<string> warnings)
    {
        var plugins = EnsureObject(globalSettings, "Plugins");
        if (ConfigDocumentValidator.Get(plugins, "PluginEnabled") is JsonObject enabled)
        {
            plugins["EnabledIntent"] = enabled.DeepClone();
        }
        else
        {
            plugins["EnabledIntent"] = new JsonObject();
        }

        var typedSettings = new JsonObject();
        if (ConfigDocumentValidator.Get(plugins, "PluginSettings") is JsonObject pluginSettings)
        {
            foreach (var (pluginId, rawValues) in pluginSettings)
            {
                if (rawValues is not JsonObject values)
                {
                    continue;
                }

                var typedValues = new JsonObject();
                foreach (var (key, rawValue) in values)
                {
                    if (rawValue is JsonValue scalar && scalar.TryGetValue<string>(out var stringValue))
                    {
                        if (string.Equals(pluginId, ConfigSchema.MineRewindPluginId, StringComparison.OrdinalIgnoreCase)
                            && MineRewindBooleanSettings.Contains(key, StringComparer.OrdinalIgnoreCase))
                        {
                            if (TryParseLegacyBoolean(stringValue, out var booleanValue))
                            {
                                typedValues[key] = booleanValue;
                            }
                            else
                            {
                                typedValues[key] = stringValue;
                                warnings.Add($"MineRewind setting '{key}' was not a valid boolean and was preserved as text.");
                            }
                        }
                        else
                        {
                            typedValues[key] = stringValue;
                        }
                    }
                    else
                    {
                        typedValues[key] = rawValue?.DeepClone();
                    }
                }

                typedSettings[pluginId] = typedValues;
            }
        }

        plugins["TypedSettings"] = typedSettings;
        RemoveProperty(plugins, "Enabled");
        RemoveProperty(plugins, "PluginEnabled");
        RemoveProperty(plugins, "PluginSettings");
        RemoveProperty(plugins, "StoreRepo");
    }

    private void EnsureConfigId(JsonObject config, HashSet<string> usedIds)
    {
        var id = ConfigDocumentValidator.GetString(config, "Id")?.Trim();
        if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
        {
            do
            {
                id = _newGuid().ToString();
            }
            while (!usedIds.Add(id));

            config["Id"] = id;
        }
    }

    private void EnsureFolderId(JsonObject folder, HashSet<Guid> usedIds)
    {
        var idText = ConfigDocumentValidator.GetString(folder, "Id");
        if (!Guid.TryParse(idText, out var id) || id == Guid.Empty || !usedIds.Add(id))
        {
            do
            {
                id = _newGuid();
            }
            while (id == Guid.Empty || !usedIds.Add(id));

            folder["Id"] = id.ToString();
        }
    }

    private static JsonObject Kind(string ownerId, string kindId)
        => new()
        {
            ["OwnerId"] = ownerId,
            ["KindId"] = kindId
        };

    private static void AppendLegacyWarning(JsonObject config, string warning)
    {
        var preservation = EnsureObject(config, "LegacyPreservation");
        var warningArray = EnsureArray(preservation, "Warnings");
        warningArray.Add(warning);
    }

    private static void CopyStringIfPresent(JsonObject source, string propertyName, JsonObject destination)
    {
        var value = ConfigDocumentValidator.GetString(source, propertyName);
        if (value is not null)
        {
            destination[propertyName] = value;
        }
    }

    private static bool TryParseLegacyBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (value == "1")
        {
            result = true;
            return true;
        }

        if (value == "0")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    internal static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (ConfigDocumentValidator.Get(parent, propertyName) is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    internal static JsonArray EnsureArray(JsonObject parent, string propertyName)
    {
        if (ConfigDocumentValidator.Get(parent, propertyName) is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        parent[propertyName] = created;
        return created;
    }

    private static void RemoveProperty(JsonObject parent, string propertyName)
    {
        var key = parent.Select(pair => pair.Key).FirstOrDefault(key =>
            string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (key is not null) parent.Remove(key);
    }
}
