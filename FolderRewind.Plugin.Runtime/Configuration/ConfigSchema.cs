using System.Text.Json;
using System.Text.Json.Nodes;

namespace FolderRewind.Plugin.Runtime.Configuration;

public static class ConfigSchema
{
    public const int CurrentVersion = 1;
    public const string VersionPropertyName = "schemaVersion";
    public const string CoreOwnerId = "folderrewind.core";
    public const string CoreDefaultKindId = "default";
    public const string MineRewindPluginId = "com.folderrewind.minerewind";
    public const string MineRewindKindId = "minecraft-saves";
    public const string MineRewindSelectedRegionsScopeId = "selected-regions";
}

public enum ConfigDocumentKind
{
    Legacy,
    Current,
    Malformed,
    UnsupportedNewer
}

public sealed record ConfigDocumentParseResult(
    ConfigDocumentKind Kind,
    JsonObject? Document,
    string DiagnosticCode = "",
    string DiagnosticMessage = "",
    int? SchemaVersion = null)
{
    public bool CanRead => Kind is ConfigDocumentKind.Legacy or ConfigDocumentKind.Current;
}

public static class ConfigDocumentParser
{
    public static ConfigDocumentParseResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var node = JsonNode.Parse(utf8Json);
            if (node is not JsonObject root)
            {
                return Malformed("config_root_not_object", "The configuration root must be a JSON object.");
            }

            if (!root.TryGetPropertyValue(ConfigSchema.VersionPropertyName, out var versionNode))
            {
                return new ConfigDocumentParseResult(ConfigDocumentKind.Legacy, root, SchemaVersion: 0);
            }

            if (versionNode is not JsonValue versionValue
                || !versionValue.TryGetValue<int>(out var version)
                || version < 0)
            {
                return Malformed("config_schema_version_invalid", "schemaVersion must be a non-negative integer.");
            }

            if (version == 0)
            {
                return new ConfigDocumentParseResult(ConfigDocumentKind.Legacy, root, SchemaVersion: version);
            }

            if (version > ConfigSchema.CurrentVersion)
            {
                return new ConfigDocumentParseResult(
                    ConfigDocumentKind.UnsupportedNewer,
                    root,
                    "config_schema_newer_than_host",
                    $"Configuration schema {version} is newer than supported schema {ConfigSchema.CurrentVersion}.",
                    version);
            }

            return new ConfigDocumentParseResult(ConfigDocumentKind.Current, root, SchemaVersion: version);
        }
        catch (JsonException ex)
        {
            return Malformed("config_json_malformed", ex.Message);
        }
        catch (Exception ex)
        {
            return Malformed("config_read_failed", ex.Message);
        }
    }

    private static ConfigDocumentParseResult Malformed(string code, string message)
        => new(ConfigDocumentKind.Malformed, null, code, message);
}
