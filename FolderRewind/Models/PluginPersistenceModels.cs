using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Models;

/// <summary>
/// Persisted Config Kind identity. This DTO mirrors ConfigKindRef without
/// coupling the application configuration file to CLR serializer details.
/// </summary>
public sealed class ConfigKindReference
{
    private string _ownerId = "folderrewind.core";
    public string OwnerId { get => _ownerId; set => ConfigMutationProtection.Set(this, ref _ownerId, value); }
    private string _kindId = "default";
    public string KindId { get => _kindId; set => ConfigMutationProtection.Set(this, ref _kindId, value); }
}

public sealed class ArtifactTransformerReference
{
    public string PluginId { get; set; } = string.Empty;
    public string TransformerId { get; set; } = string.Empty;
}

public enum PersistedArtifactTransformFailureBehavior
{
    KeepPrimaryWithWarnings = 0,
    RequireTransform = 1
}

public sealed class ArtifactTransformPolicySettings
{
    public ArtifactTransformerReference Transformer { get; set; } = new();
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
    public PersistedArtifactTransformFailureBehavior FailureBehavior { get; set; }
}

public enum PersistedConsistencyIntent
{
    Prefer = 0,
    Require = 1
}

/// <summary>
/// Opaque state owned and migrated exclusively by a State Owner.
/// </summary>
public sealed class ProviderStatePayload
{
    private int _schemaVersion;
    private JsonElement _data = EmptyObject();
    public int SchemaVersion { get => _schemaVersion; set => ConfigMutationProtection.Set(this, ref _schemaVersion, value); }
    public JsonElement Data { get => _data; set => ConfigMutationProtection.Set(this, ref _data, value); }

    private static JsonElement EmptyObject()
        => JsonDocument.Parse("{}").RootElement.Clone();
}

/// <summary>
/// Host-owned provenance, deliberately separate from provider state.
/// </summary>
public sealed class HostConfigOrigin
{
    public string TemplateId { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string DiscoveryProviderId { get; set; } = string.Empty;
    public string DiscoveryCandidateId { get; set; } = string.Empty;
}

/// <summary>
/// Read-only legacy material retained so an unknown v2 field is never lost.
/// Product code must not interpret this as active v3 provider state.
/// </summary>
public sealed class LegacyConfigPreservation
{
    public string OriginalConfigType { get; set; } = string.Empty;
    public string OriginalPluginMarker { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> OriginalExtendedProperties { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum PersistedOperationOutcome
{
    Unknown = 0,
    Success = 1,
    SuccessWithWarnings = 2,
    NoChanges = 3,
    Canceled = 4,
    Failed = 5,
    Blocked = 6
}

public enum PersistedDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed class OperationDiagnosticRecord
{
    public string Code { get; set; } = string.Empty;
    public PersistedDiagnosticSeverity Severity { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Provider defaults are exported only when their schema marks them shareable.
/// </summary>
public sealed class PresetProviderDefaults
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
}
