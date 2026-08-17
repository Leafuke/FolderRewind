using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public sealed record ParsedPluginPackageManifest(
    PluginManifestContract Contract,
    IReadOnlyList<string> Architectures,
    string Author,
    string Homepage,
    string Repository);

public static class PluginPackageManifestReader
{
    public static ParsedPluginPackageManifest Parse(ReadOnlySpan<byte> utf8)
    {
        var dto = JsonSerializer.Deserialize<ManifestDto>(utf8, JsonOptions())
            ?? throw new InvalidDataException("Plugin manifest is empty.");
        if (dto.ManifestVersion != 3) throw new InvalidDataException("Only manifestVersion 3 is supported.");
        var pluginId = new PluginId(dto.PluginId);
        var version = PluginSemanticVersion.RequireStrict(dto.Version, "Plugin version");
        var api = dto.PluginApi ?? throw new InvalidDataException("pluginApi is required.");
        var contract = new PluginManifestContract(
            pluginId,
            version,
            new PluginApiVersion(api.Major, api.Minor),
            RequireRelativeFile(dto.EntryAssembly, "entryAssembly"),
            Require(dto.EntryType, "entryType"),
            Text(dto.Name, dto.LocalizedName),
            Text(dto.Description, dto.LocalizedDescription),
            (dto.ConfigKinds ?? []).Select(value => new ConfigKindDeclaration(
                new ConfigKindRef(new OwnerId(value.OwnerId), value.KindId),
                Text(value.DisplayName, value.LocalizedDisplayName),
                Text(value.Description, value.LocalizedDescription),
                value.Icon ?? string.Empty,
                ParseEnum<BackupFallbackPolicy>(value.BackupFallback),
                ParseEnum<RestoreCoordinationPolicy>(value.RestoreCoordination))).ToArray(),
            RequireRelativeFile(dto.SettingsSchema, "settingsSchema"),
            (dto.RequestedHostServices ?? []).Select(ParseEnum<HostServiceKind>).ToArray(),
            (dto.Capabilities ?? []).Select(ParseEnum<PluginCapabilityKind>).ToArray(),
            (dto.ArtifactFormats ?? []).Select(value => new ArtifactFormatDeclaration(
                new ArtifactFormatRef(new OwnerId(value.OwnerId), value.FormatId),
                value.MinimumVersion,
                value.MaximumVersion,
                Text(value.DisplayName, value.LocalizedDisplayName))).ToArray(),
            (dto.ArtifactTransformers ?? []).Select(value => new ArtifactTransformerDeclaration(
                new ArtifactTransformerId(pluginId, value.TransformerId),
                (value.CompatibleConfigKinds ?? []).Select(kind =>
                    new ConfigKindRef(new OwnerId(kind.OwnerId), kind.KindId)).ToArray(),
                (value.CompatibleCoreModes ?? []).Select(ParseEnum<CoreCaptureMode>).ToArray(),
                (value.CompatibleCompleteness ?? []).Select(ParseEnum<ArtifactCompleteness>).ToArray(),
                value.ParameterSchema.ValueKind == JsonValueKind.Undefined
                    ? JsonDocument.Parse("{}").RootElement.Clone()
                    : value.ParameterSchema.Clone(),
                (value.SupportedFailureBehaviors ?? []).Select(ParseEnum<ArtifactTransformFailureBehavior>).ToArray())).ToArray(),
            (dto.RestoreStrategies ?? []).Select(value => new RestoreStrategyDeclaration(
                new RestoreStrategyId(pluginId, value.StrategyId),
                (value.SupportedFormats ?? []).Select(format => new ArtifactFormatVersionRange(
                    new ArtifactFormatRef(new OwnerId(format.OwnerId), format.FormatId),
                    format.MinimumVersion,
                    format.MaximumVersion)).ToArray(),
                (value.SupportedCompleteness ?? []).Select(ParseEnum<ArtifactCompleteness>).ToArray(),
                (value.SupportedRestoreModes ?? []).Select(ParseEnum<RestoreMode>).ToArray())).ToArray(),
            dto.HasBackupCompletionObserver);
        return new ParsedPluginPackageManifest(
            contract,
            (dto.Architectures ?? ["any"]).Select(value => value.Trim().ToLowerInvariant()).Distinct().ToArray(),
            dto.Author ?? string.Empty,
            dto.Homepage ?? string.Empty,
            dto.Repository ?? string.Empty);
    }

    private static JsonSerializerOptions JsonOptions()
        => new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    private static LocalizedText Text(string? value, Dictionary<string, string>? localized)
        => new(Require(value, "localized text default"), localized ?? new Dictionary<string, string>());

    private static T ParseEnum<T>(string? value) where T : struct, Enum
    {
        var normalized = value?.Replace("-", string.Empty).Replace("_", string.Empty) ?? string.Empty;
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new InvalidDataException($"Unknown {typeof(T).Name} value '{value}'.");
    }

    private static string Require(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{field} is required.")
            : value.Trim();

    private static string RequireRelativeFile(string? value, string field)
    {
        var path = Require(value, field).Replace('\\', '/');
        if (Path.IsPathRooted(path) || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"{field} must be a canonical relative file path.");
        return path;
    }

    private sealed class ManifestDto
    {
        public int ManifestVersion { get; set; }
        public string PluginId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Name { get; set; }
        public Dictionary<string, string>? LocalizedName { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, string>? LocalizedDescription { get; set; }
        public string? Author { get; set; }
        public string? Homepage { get; set; }
        public string? Repository { get; set; }
        public ApiDto? PluginApi { get; set; }
        public string? EntryAssembly { get; set; }
        public string? EntryType { get; set; }
        public string? SettingsSchema { get; set; }
        public List<string>? Architectures { get; set; }
        public List<KindDto>? ConfigKinds { get; set; }
        public List<string>? RequestedHostServices { get; set; }
        public List<string>? Capabilities { get; set; }
        public List<FormatDto>? ArtifactFormats { get; set; }
        public List<TransformerDto>? ArtifactTransformers { get; set; }
        public List<StrategyDto>? RestoreStrategies { get; set; }
        public bool HasBackupCompletionObserver { get; set; }
    }
    private sealed class ApiDto { public int Major { get; set; } public int Minor { get; set; } }
    private sealed class KindDto
    {
        public string OwnerId { get; set; } = string.Empty;
        public string KindId { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public Dictionary<string, string>? LocalizedDisplayName { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, string>? LocalizedDescription { get; set; }
        public string? Icon { get; set; }
        public string? BackupFallback { get; set; }
        public string? RestoreCoordination { get; set; }
    }
    private class FormatDto
    {
        public string OwnerId { get; set; } = string.Empty;
        public string FormatId { get; set; } = string.Empty;
        public int MinimumVersion { get; set; }
        public int MaximumVersion { get; set; }
        public string? DisplayName { get; set; }
        public Dictionary<string, string>? LocalizedDisplayName { get; set; }
    }
    private sealed class KindRefDto { public string OwnerId { get; set; } = string.Empty; public string KindId { get; set; } = string.Empty; }
    private sealed class TransformerDto
    {
        public string TransformerId { get; set; } = string.Empty;
        public List<KindRefDto>? CompatibleConfigKinds { get; set; }
        public List<string>? CompatibleCoreModes { get; set; }
        public List<string>? CompatibleCompleteness { get; set; }
        public JsonElement ParameterSchema { get; set; }
        public List<string>? SupportedFailureBehaviors { get; set; }
    }
    private sealed class FormatRangeDto : FormatDto;
    private sealed class StrategyDto
    {
        public string StrategyId { get; set; } = string.Empty;
        public List<FormatRangeDto>? SupportedFormats { get; set; }
        public List<string>? SupportedCompleteness { get; set; }
        public List<string>? SupportedRestoreModes { get; set; }
    }
}
