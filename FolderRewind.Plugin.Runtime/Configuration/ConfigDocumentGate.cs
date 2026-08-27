using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FolderRewind.Plugin.Runtime.Configuration;

public enum ConfigDocumentGateStatus
{
    Current,
    Migrated,
    RecoveryRequired
}

public sealed record ConfigDocumentGateResult(
    ConfigDocumentGateStatus Status,
    JsonObject? Document,
    byte[]? Utf8Json,
    string DiagnosticCode = "",
    string DiagnosticMessage = "",
    IReadOnlyList<string>? Warnings = null)
{
    public bool IsReady => Status is ConfigDocumentGateStatus.Current or ConfigDocumentGateStatus.Migrated;
}

public sealed class ConfigDocumentGate
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly LegacyConfigMigrator _migrator;

    public ConfigDocumentGate(LegacyConfigMigrator? migrator = null)
    {
        _migrator = migrator ?? new LegacyConfigMigrator();
    }

    public ConfigDocumentGateResult Prepare(ReadOnlySpan<byte> utf8Json)
    {
        var parse = ConfigDocumentParser.Parse(utf8Json);
        if (!parse.CanRead || parse.Document is null)
        {
            return Recovery(parse.DiagnosticCode, parse.DiagnosticMessage);
        }

        if (parse.Kind == ConfigDocumentKind.Current)
        {
            var normalization = _migrator.NormalizeCurrentIdentities(parse.Document);
            var validation = ConfigDocumentValidator.Validate(normalization.Document);
            if (!validation.IsValid)
            {
                return RecoveryFromValidation(validation);
            }

            if (normalization.Warnings.Count > 0)
            {
                var normalizedJson = normalization.Document.ToJsonString(SerializerOptions);
                return new ConfigDocumentGateResult(
                    ConfigDocumentGateStatus.Migrated,
                    normalization.Document,
                    Encoding.UTF8.GetBytes(normalizedJson),
                    Warnings: normalization.Warnings);
            }

            return new ConfigDocumentGateResult(
                ConfigDocumentGateStatus.Current,
                parse.Document,
                utf8Json.ToArray());
        }

        try
        {
            var migration = _migrator.Migrate(parse.Document);
            var validation = ConfigDocumentValidator.Validate(migration.Document);
            if (!validation.IsValid)
            {
                return RecoveryFromValidation(validation);
            }

            var json = migration.Document.ToJsonString(SerializerOptions);
            return new ConfigDocumentGateResult(
                ConfigDocumentGateStatus.Migrated,
                migration.Document,
                Encoding.UTF8.GetBytes(json),
                Warnings: migration.Warnings);
        }
        catch (Exception ex)
        {
            return Recovery("config_migration_failed", ex.Message);
        }
    }

    private static ConfigDocumentGateResult RecoveryFromValidation(ConfigValidationResult validation)
    {
        var first = validation.Issues[0];
        return Recovery(first.Code, $"{first.JsonPath}: {first.Message}");
    }

    private static ConfigDocumentGateResult Recovery(string code, string message)
        => new(ConfigDocumentGateStatus.RecoveryRequired, null, null, code, message);
}
