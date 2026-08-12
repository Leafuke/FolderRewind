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
    public string OwnerId { get; set; } = "folderrewind.core";
    public string KindId { get; set; } = "default";
}

/// <summary>
/// Opaque state owned and migrated exclusively by a State Owner.
/// </summary>
public sealed class ProviderStatePayload
{
    public int SchemaVersion { get; set; }
    public JsonElement Data { get; set; } = EmptyObject();

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
