using System.Text.RegularExpressions;

namespace FolderRewind.Plugin.Abstractions;

public readonly record struct PluginId
{
    public PluginId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct OwnerId
{
    public OwnerId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct DiscoveryProviderId
{
    public DiscoveryProviderId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct StateOwnerId
{
    public StateOwnerId(string value) => Value = PluginIdentitySyntax.RequireOwner(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ConfigKindRef
{
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

public readonly record struct PluginCommandId
{
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
