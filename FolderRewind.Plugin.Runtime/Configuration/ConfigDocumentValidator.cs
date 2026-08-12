using System.Text.Json.Nodes;

namespace FolderRewind.Plugin.Runtime.Configuration;

public sealed record ConfigValidationIssue(string Code, string JsonPath, string Message);

public sealed class ConfigValidationResult
{
    public required IReadOnlyList<ConfigValidationIssue> Issues { get; init; }
    public bool IsValid => Issues.Count == 0;
}

public static class ConfigDocumentValidator
{
    public static ConfigValidationResult Validate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var issues = new List<ConfigValidationIssue>();

        if (!TryGetInt(root, ConfigSchema.VersionPropertyName, out var version)
            || version != ConfigSchema.CurrentVersion)
        {
            issues.Add(new("config_schema_version_invalid", "$.schemaVersion", "The document is not schema 1."));
        }

        if (Get(root, "GlobalSettings") is not JsonObject)
        {
            issues.Add(new("config_global_settings_missing", "$.GlobalSettings", "GlobalSettings must be an object."));
        }

        if (Get(root, "BackupConfigs") is not JsonArray configs)
        {
            issues.Add(new("config_backup_configs_missing", "$.BackupConfigs", "BackupConfigs must be an array."));
            return new ConfigValidationResult { Issues = issues };
        }

        var configIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderIds = new HashSet<Guid>();
        for (var configIndex = 0; configIndex < configs.Count; configIndex++)
        {
            var path = $"$.BackupConfigs[{configIndex}]";
            if (configs[configIndex] is not JsonObject config)
            {
                issues.Add(new("config_entry_invalid", path, "Backup configuration entries must be objects."));
                continue;
            }

            var configId = GetString(config, "Id")?.Trim();
            if (string.IsNullOrWhiteSpace(configId))
            {
                issues.Add(new("config_id_missing", path + ".Id", "Backup configuration Id is required."));
            }
            else if (!configIds.Add(configId))
            {
                issues.Add(new("config_id_duplicate", path + ".Id", "Backup configuration Id must be unique."));
            }

            ValidateKind(config, path, issues);
            ValidateProviderStates(Get(config, "ProviderStates"), path + ".ProviderStates", issues);
            ValidateScope(Get(config, "BackupScope"), path + ".BackupScope", issues);

            if (Get(config, "SourceFolders") is not JsonArray folders)
            {
                issues.Add(new("config_source_folders_invalid", path + ".SourceFolders", "SourceFolders must be an array."));
                continue;
            }

            for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
            {
                var folderPath = $"{path}.SourceFolders[{folderIndex}]";
                if (folders[folderIndex] is not JsonObject folder)
                {
                    issues.Add(new("folder_entry_invalid", folderPath, "Managed folder entries must be objects."));
                    continue;
                }

                var idText = GetString(folder, "Id");
                if (!Guid.TryParse(idText, out var id) || id == Guid.Empty)
                {
                    issues.Add(new("folder_id_invalid", folderPath + ".Id", "Managed folder Id must be a non-empty GUID."));
                }
                else if (!folderIds.Add(id))
                {
                    issues.Add(new("folder_id_duplicate", folderPath + ".Id", "Managed folder Id must be unique."));
                }

                if (Get(folder, "Path") is not JsonValue pathValue || !pathValue.TryGetValue<string>(out _))
                {
                    issues.Add(new("folder_path_invalid", folderPath + ".Path", "Managed folder Path must be a string."));
                }

                ValidateProviderStates(Get(folder, "ProviderStates"), folderPath + ".ProviderStates", issues);
            }
        }

        return new ConfigValidationResult { Issues = issues };
    }

    private static void ValidateKind(JsonObject config, string path, List<ConfigValidationIssue> issues)
    {
        if (Get(config, "Kind") is not JsonObject kind
            || string.IsNullOrWhiteSpace(GetString(kind, "OwnerId"))
            || string.IsNullOrWhiteSpace(GetString(kind, "KindId")))
        {
            issues.Add(new("config_kind_invalid", path + ".Kind", "Kind must contain non-empty OwnerId and KindId."));
        }
    }

    private static void ValidateScope(JsonNode? node, string path, List<ConfigValidationIssue> issues)
    {
        if (node is null)
        {
            return;
        }

        if (node is not JsonObject scope)
        {
            issues.Add(new("backup_scope_invalid", path, "BackupScope must be an object."));
            return;
        }

        var ownerId = GetString(scope, "OwnerId")?.Trim() ?? string.Empty;
        var scopeId = GetString(scope, "ScopeId")?.Trim() ?? string.Empty;
        if (ownerId.Length == 0 ^ scopeId.Length == 0)
        {
            issues.Add(new("backup_scope_identity_incomplete", path, "OwnerId and ScopeId must either both be present or both be empty."));
        }

        if (Get(scope, "Parameters") is not null and not JsonObject)
        {
            issues.Add(new("backup_scope_parameters_invalid", path + ".Parameters", "Scope Parameters must be an object."));
        }
    }

    private static void ValidateProviderStates(JsonNode? node, string path, List<ConfigValidationIssue> issues)
    {
        if (node is null)
        {
            return;
        }

        if (node is not JsonObject states)
        {
            issues.Add(new("provider_states_invalid", path, "ProviderStates must be an object."));
            return;
        }

        foreach (var (ownerId, stateNode) in states)
        {
            var statePath = path + "." + ownerId;
            if (string.IsNullOrWhiteSpace(ownerId) || stateNode is not JsonObject state)
            {
                issues.Add(new("provider_state_invalid", statePath, "Provider state owner and wrapper must be valid."));
                continue;
            }

            if (!TryGetInt(state, "SchemaVersion", out var stateVersion) || stateVersion < 0)
            {
                issues.Add(new("provider_state_schema_invalid", statePath + ".SchemaVersion", "Provider state SchemaVersion must be non-negative."));
            }

            if (Get(state, "Data") is null)
            {
                issues.Add(new("provider_state_data_missing", statePath + ".Data", "Provider state Data is required."));
            }
        }
    }

    internal static JsonNode? Get(JsonObject value, string propertyName)
    {
        if (value.TryGetPropertyValue(propertyName, out var exact))
        {
            return exact;
        }

        foreach (var property in value)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    internal static string? GetString(JsonObject value, string propertyName)
        => Get(value, propertyName) is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    internal static bool TryGetInt(JsonObject value, string propertyName, out int result)
    {
        result = 0;
        return Get(value, propertyName) is JsonValue jsonValue && jsonValue.TryGetValue<int>(out result);
    }
}
