using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace FolderRewind.Plugin.Abstractions;

public readonly record struct PluginId
{
    [JsonConstructor]
    public PluginId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct OwnerId
{
    [JsonConstructor]
    public OwnerId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct DiscoveryProviderId
{
    [JsonConstructor]
    public DiscoveryProviderId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct StateOwnerId
{
    [JsonConstructor]
    public StateOwnerId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ConfigKindRef
{
    [JsonConstructor]
    public ConfigKindRef(OwnerId ownerId, string kindId)
    {
        if (string.IsNullOrWhiteSpace(ownerId.Value)) throw new ArgumentException("OwnerId is required.", nameof(ownerId));
        OwnerId = ownerId;
        KindId = PluginIdentitySyntax.RequireLocal(kindId, nameof(kindId));
    }

    public OwnerId OwnerId { get; }
    public string KindId { get; }
    public override string ToString() => $"{OwnerId}/{KindId}";
}

public readonly record struct ConfigRevision
{
    [JsonConstructor]
    public ConfigRevision(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Config revision is required.", nameof(value))
            : value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ArtifactTransformerId
{
    [JsonConstructor]
    public ArtifactTransformerId(PluginId pluginId, string transformerId)
    {
        if (string.IsNullOrWhiteSpace(pluginId.Value)) throw new ArgumentException("PluginId is required.", nameof(pluginId));
        PluginId = pluginId;
        TransformerId = PluginIdentitySyntax.RequireLocal(transformerId, nameof(transformerId));
    }

    public PluginId PluginId { get; }
    public string TransformerId { get; }
    public override string ToString() => $"{PluginId}/{TransformerId}";
}

public readonly record struct ArtifactId
{
    [JsonConstructor]
    public ArtifactId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("ArtifactId cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ArtifactGraphRevision
{
    [JsonConstructor]
    public ArtifactGraphRevision(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Artifact graph revision is required.", nameof(value))
            : value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ArtifactFormatRef
{
    [JsonConstructor]
    public ArtifactFormatRef(OwnerId ownerId, string formatId)
    {
        if (string.IsNullOrWhiteSpace(ownerId.Value)) throw new ArgumentException("OwnerId is required.", nameof(ownerId));
        OwnerId = ownerId;
        FormatId = PluginIdentitySyntax.RequireLocal(formatId, nameof(formatId));
    }

    public OwnerId OwnerId { get; }
    public string FormatId { get; }
    public override string ToString() => $"{OwnerId}/{FormatId}";
}

public readonly record struct RestoreStrategyId
{
    [JsonConstructor]
    public RestoreStrategyId(PluginId pluginId, string strategyId)
    {
        if (string.IsNullOrWhiteSpace(pluginId.Value)) throw new ArgumentException("PluginId is required.", nameof(pluginId));
        PluginId = pluginId;
        StrategyId = PluginIdentitySyntax.RequireLocal(strategyId, nameof(strategyId));
    }

    public PluginId PluginId { get; }
    public string StrategyId { get; }
    public override string ToString() => $"{PluginId}/{StrategyId}";
}

public readonly record struct ArtifactContentHandle
{
    [JsonConstructor]
    public ArtifactContentHandle(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Artifact content handle is required.", nameof(value))
            : value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ArtifactStagingHandle
{
    [JsonConstructor]
    public ArtifactStagingHandle(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Artifact staging handle is required.", nameof(value))
            : value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct PluginCommandId
{
    [JsonConstructor]
    public PluginCommandId(PluginId pluginId, string commandId)
    {
        if (string.IsNullOrWhiteSpace(pluginId.Value)) throw new ArgumentException("PluginId is required.", nameof(pluginId));
        PluginId = pluginId;
        CommandId = PluginIdentitySyntax.RequireLocal(commandId, nameof(commandId));
    }

    public PluginId PluginId { get; }
    public string CommandId { get; }
    public override string ToString() => $"{PluginId}/{CommandId}";
}

public readonly record struct BackupScopeId
{
    [JsonConstructor]
    public BackupScopeId(OwnerId ownerId, string scopeId)
    {
        if (string.IsNullOrWhiteSpace(ownerId.Value)) throw new ArgumentException("OwnerId is required.", nameof(ownerId));
        OwnerId = ownerId;
        ScopeId = PluginIdentitySyntax.RequireLocal(scopeId, nameof(scopeId));
    }

    public OwnerId OwnerId { get; }
    public string ScopeId { get; }
    public override string ToString() => $"{OwnerId}/{ScopeId}";
}

internal static partial class PluginIdentitySyntax
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerRegex();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalRegex();

    public static string RequireOwner(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!OwnerRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Identity must be a lowercase reverse-domain name.", parameterName);
        }

        return normalized;
    }

    public static string RequireLocal(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!LocalRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Local identity must contain lowercase ASCII letters, digits, dots, or hyphens.", parameterName);
        }

        return normalized;
    }
}
