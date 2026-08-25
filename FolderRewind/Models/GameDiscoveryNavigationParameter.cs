namespace FolderRewind.Models;

public sealed class GameDiscoveryNavigationParameter
{
    public DiscoveryRequestMode Mode { get; init; } = DiscoveryRequestMode.FullMachine;
    public string PresetShareId { get; init; } = string.Empty;
    public string RequestedConfigName { get; init; } = string.Empty;

    public static GameDiscoveryNavigationParameter ForPreset(
        string presetShareId,
        string? requestedConfigName = null) => new()
    {
        Mode = DiscoveryRequestMode.PresetTargeted,
        PresetShareId = presetShareId ?? string.Empty,
        RequestedConfigName = requestedConfigName?.Trim() ?? string.Empty
    };
}
