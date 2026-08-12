using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Settings;

public enum PluginSettingValueType
{
    String,
    Boolean,
    Integer,
    Multiline,
    FolderPath,
    FilePath,
    Enum
}

public sealed record PluginSettingSchemaDefinition(
    string Key,
    PluginSettingValueType Type,
    bool Required,
    JsonElement? DefaultValue,
    IReadOnlyList<string> EnumValues,
    string DisplayName,
    string Description);

public sealed class PluginSettingsSchema
{
    private readonly IReadOnlyDictionary<string, PluginSettingSchemaDefinition> _byKey;

    private PluginSettingsSchema(int schemaVersion, IReadOnlyList<PluginSettingSchemaDefinition> settings)
    {
        SchemaVersion = schemaVersion;
        Settings = settings;
        _byKey = settings.ToDictionary(setting => setting.Key, StringComparer.Ordinal);
    }

    public int SchemaVersion { get; }
    public IReadOnlyList<PluginSettingSchemaDefinition> Settings { get; }

    public static PluginSettingsSchema Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var versionNode)
                || !versionNode.TryGetInt32(out var version)
                || version != 1)
            {
                throw new InvalidDataException("Plugin settings schemaVersion must be 1.");
            }

            if (!root.TryGetProperty("settings", out var settingsNode) || settingsNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Plugin settings schema requires a settings array.");
            }

            var settings = new List<PluginSettingSchemaDefinition>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in settingsNode.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Plugin setting definitions must be objects.");
                }

                var key = RequiredString(node, "key");
                if (!IsValidKey(key) || !keys.Add(key))
                {
                    throw new InvalidDataException($"Plugin setting key '{key}' is invalid or duplicated.");
                }

                var type = ParseType(RequiredString(node, "type"));
                var required = false;
                if (node.TryGetProperty("required", out var requiredNode))
                {
                    if (requiredNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidDataException($"Required flag for setting '{key}' must be boolean.");
                    }
                    required = requiredNode.GetBoolean();
                }
                JsonElement? defaultValue = node.TryGetProperty("default", out var defaultNode)
                    ? defaultNode.Clone()
                    : null;
                var enumValues = ReadEnumValues(node, type);
                var definition = new PluginSettingSchemaDefinition(
                    key,
                    type,
                    required,
                    defaultValue,
                    enumValues,
                    OptionalString(node, "displayName"),
                    OptionalString(node, "description"));
                if (defaultValue.HasValue && !IsValueValid(definition, defaultValue.Value))
                {
                    throw new InvalidDataException($"Default value for setting '{key}' does not match its type.");
                }
                settings.Add(definition);
            }

            return new PluginSettingsSchema(version, settings);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Plugin settings schema is not valid JSON.", ex);
        }
    }

    public PluginSettingsValidationResult Validate(PluginSettingsSnapshot candidate)
    {
        var normalized = candidate.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        var issues = new List<PluginSettingsValidationIssue>();

        foreach (var unknown in normalized.Keys.Where(key => !_byKey.ContainsKey(key)))
        {
            issues.Add(new PluginSettingsValidationIssue(
                "settings.unknown_preserved",
                DiagnosticSeverity.Warning,
                unknown,
                "Unknown setting was preserved for forward and legacy compatibility."));
        }

        foreach (var definition in Settings)
        {
            if (!normalized.TryGetValue(definition.Key, out var value))
            {
                if (definition.DefaultValue.HasValue)
                {
                    normalized[definition.Key] = definition.DefaultValue.Value.Clone();
                }
                else if (definition.Required)
                {
                    issues.Add(new PluginSettingsValidationIssue(
                        "settings.required",
                        DiagnosticSeverity.Error,
                        definition.Key,
                        "A required setting is missing."));
                }
                continue;
            }

            if (!IsValueValid(definition, value))
            {
                issues.Add(new PluginSettingsValidationIssue(
                    "settings.type_invalid",
                    DiagnosticSeverity.Error,
                    definition.Key,
                    $"The value does not satisfy setting type {definition.Type}."));
            }
        }

        return new PluginSettingsValidationResult(
            new PluginSettingsSnapshot(candidate.PluginId, normalized),
            issues);
    }

    private static bool IsValueValid(PluginSettingSchemaDefinition definition, JsonElement value)
        => definition.Type switch
        {
            PluginSettingValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            PluginSettingValueType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            PluginSettingValueType.String or PluginSettingValueType.Multiline
                or PluginSettingValueType.FolderPath or PluginSettingValueType.FilePath
                => value.ValueKind == JsonValueKind.String,
            PluginSettingValueType.Enum => value.ValueKind == JsonValueKind.String
                                           && definition.EnumValues.Contains(value.GetString() ?? string.Empty, StringComparer.Ordinal),
            _ => false
        };

    private static IReadOnlyList<string> ReadEnumValues(JsonElement node, PluginSettingValueType type)
    {
        if (type != PluginSettingValueType.Enum)
        {
            return Array.Empty<string>();
        }

        if (!node.TryGetProperty("enumValues", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Enum settings require enumValues.");
        }
        var result = values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : throw new InvalidDataException("Enum values must be strings."))
            .ToArray();
        if (result.Length == 0 || result.Any(string.IsNullOrWhiteSpace) || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new InvalidDataException("Enum values must be non-empty and unique.");
        }
        return result;
    }

    private static PluginSettingValueType ParseType(string type)
        => type switch
        {
            "string" => PluginSettingValueType.String,
            "boolean" => PluginSettingValueType.Boolean,
            "integer" => PluginSettingValueType.Integer,
            "multiline" => PluginSettingValueType.Multiline,
            "folderPath" => PluginSettingValueType.FolderPath,
            "filePath" => PluginSettingValueType.FilePath,
            "enum" => PluginSettingValueType.Enum,
            _ => throw new InvalidDataException($"Unsupported plugin setting type '{type}'.")
        };

    private static string RequiredString(JsonElement node, string property)
    {
        var value = OptionalString(node, property);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Plugin setting property '{property}' is required.")
            : value;
    }

    private static string OptionalString(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool IsValidKey(string key)
        => key.Length is > 0 and <= 128
           && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

public sealed record PluginSettingsValidationIssue(
    string Code,
    DiagnosticSeverity Severity,
    string Key,
    string Message);

public sealed record PluginSettingsValidationResult(
    PluginSettingsSnapshot NormalizedSettings,
    IReadOnlyList<PluginSettingsValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != DiagnosticSeverity.Error);
}
