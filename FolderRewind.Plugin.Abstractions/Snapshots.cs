using System.Text.Json;

namespace FolderRewind.Plugin.Abstractions;

public sealed record PluginSettingsSnapshot(
    PluginId PluginId,
    IReadOnlyDictionary<string, JsonElement> Values);

public sealed record ProviderStateSnapshot(
    StateOwnerId StateOwnerId,
    int SchemaVersion,
    JsonElement Data);

public sealed record ProviderStatePatch(
    StateOwnerId StateOwnerId,
    int ExpectedSchemaVersion,
    int SchemaVersion,
    JsonElement Data);

public sealed record ConfigSnapshot(
    string ConfigId,
    ConfigKindRef Kind,
    string Name,
    IReadOnlyList<FolderSnapshot> Folders,
    IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> ProviderStates);

public sealed record FolderSnapshot(
    Guid FolderId,
    string Path,
    string DisplayName,
    IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> ProviderStates);

public sealed record PluginActivationPatch(
    IReadOnlyList<ProviderStatePatch> ProviderStatePatches)
{
    public static PluginActivationPatch Empty { get; } = new(Array.Empty<ProviderStatePatch>());
}

public sealed record PluginActivationResult(PluginActivationPatch Patch)
{
    public static PluginActivationResult Empty { get; } = new(PluginActivationPatch.Empty);
}
