namespace FolderRewind.Models;

public enum GameDiscoveryNavigationMode
{
    FullMachine = 0,
    PresetTargeted = 1,
    PluginBatch = 2
}

public sealed class GameDiscoveryNavigationParameter
{
    public GameDiscoveryNavigationMode Mode { get; init; } = GameDiscoveryNavigationMode.FullMachine;
    public string PresetShareId { get; init; } = string.Empty;
    public string RequestedConfigName { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public ConfigKindReference? ConfigKind { get; init; }
    public string UserRoot { get; init; } = string.Empty;

    public static GameDiscoveryNavigationParameter ForPreset(
        string presetShareId,
        string? requestedConfigName = null) => new()
    {
        Mode = GameDiscoveryNavigationMode.PresetTargeted,
        PresetShareId = presetShareId ?? string.Empty,
        RequestedConfigName = requestedConfigName?.Trim() ?? string.Empty
    };

    public static GameDiscoveryNavigationParameter ForPluginBatch(
        string pluginId,
        ConfigKindReference configKind,
        string userRoot) => new()
    {
        Mode = GameDiscoveryNavigationMode.PluginBatch,
        PluginId = pluginId?.Trim() ?? string.Empty,
        ConfigKind = configKind == null
            ? null
            : new ConfigKindReference
            {
                OwnerId = configKind.OwnerId,
                KindId = configKind.KindId
            },
        UserRoot = userRoot?.Trim() ?? string.Empty
    };
}
