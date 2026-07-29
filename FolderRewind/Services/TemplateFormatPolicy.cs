using System;
using System.Text.Json;

namespace FolderRewind.Services;

internal static class TemplateFormatPolicy
{
    public const string TemplateMagic = "FolderRewindTemplate";
    public const string SchemaVersion = "1.0";

    public static bool IsCurrentEnvelope(string? magic, string? schemaVersion) =>
        string.Equals(magic, TemplateMagic, StringComparison.Ordinal)
        && string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal);

    public static bool IsCurrentOfficialIndex(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("schemaVersion", out var schemaVersion)
        && schemaVersion.ValueKind == JsonValueKind.String
        && string.Equals(schemaVersion.GetString(), SchemaVersion, StringComparison.Ordinal)
        && root.TryGetProperty("templates", out var templates)
        && templates.ValueKind == JsonValueKind.Array;
}
