using System;
using System.Text.Json;

namespace FolderRewind.Services;

internal static class TemplateFormatPolicy
{
    public const string LegacyTemplateMagic = "FolderRewindTemplate";
    public const string LegacySchemaVersion = "1.0";
    public const string BackupPresetMagic = "FolderRewindBackupPreset";
    public const string BackupPresetSchemaVersion = "2.0";
    public const string BackupPresetIndexMagic = "FolderRewindBackupPresetIndex";

    public static bool IsCurrentEnvelope(string? magic, string? schemaVersion) =>
        IsLegacyEnvelope(magic, schemaVersion);

    public static bool IsLegacyEnvelope(string? magic, string? schemaVersion) =>
        string.Equals(magic, LegacyTemplateMagic, StringComparison.Ordinal)
        && string.Equals(schemaVersion, LegacySchemaVersion, StringComparison.Ordinal);

    public static bool IsBackupPresetEnvelope(string? magic, string? schemaVersion) =>
        string.Equals(magic, BackupPresetMagic, StringComparison.Ordinal)
        && string.Equals(schemaVersion, BackupPresetSchemaVersion, StringComparison.Ordinal);

    public static bool IsCurrentOfficialIndex(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("schemaVersion", out var schemaVersion)
        && schemaVersion.ValueKind == JsonValueKind.String
        && string.Equals(schemaVersion.GetString(), LegacySchemaVersion, StringComparison.Ordinal)
        && root.TryGetProperty("templates", out var templates)
        && templates.ValueKind == JsonValueKind.Array;

    public static bool IsCurrentBackupPresetIndex(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("magic", out var magic)
        && magic.ValueKind == JsonValueKind.String
        && string.Equals(magic.GetString(), BackupPresetIndexMagic, StringComparison.Ordinal)
        && root.TryGetProperty("schemaVersion", out var schemaVersion)
        && schemaVersion.ValueKind == JsonValueKind.String
        && string.Equals(schemaVersion.GetString(), BackupPresetSchemaVersion, StringComparison.Ordinal)
        && root.TryGetProperty("presets", out var presets)
        && presets.ValueKind == JsonValueKind.Array;
}
