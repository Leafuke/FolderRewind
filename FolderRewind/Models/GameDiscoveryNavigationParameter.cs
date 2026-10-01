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
    public bool ReturnDraftToSetup { get; init; }
    public System.Collections.Generic.List<BackupSetupDraftSelection> ResumingSelections { get; init; } = [];

    public static GameDiscoveryNavigationParameter ForPreset(
        string presetShareId,
        string? requestedConfigName = null,
        bool returnDraftToSetup = false) => new()
    {
        Mode = GameDiscoveryNavigationMode.PresetTargeted,
        ReturnDraftToSetup = returnDraftToSetup,
        PresetShareId = presetShareId ?? string.Empty,
        RequestedConfigName = requestedConfigName?.Trim() ?? string.Empty
    };

    public static GameDiscoveryNavigationParameter ForPluginBatch(
        string pluginId,
        ConfigKindReference configKind,
        string userRoot,
        bool returnDraftToSetup = false) => new()
    {
        Mode = GameDiscoveryNavigationMode.PluginBatch,
        ReturnDraftToSetup = returnDraftToSetup,
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
