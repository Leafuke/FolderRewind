using FolderRewind.Models;
using System;
using System.Collections.Generic;

namespace FolderRewind.Services.Plugins;

public enum PluginConfigAugmentationReason
{
    Startup = 0,
    SettingsEnabled = 1
}

public sealed class PluginConfigAugmentationRequest
{
    public PluginConfigAugmentationReason Reason { get; init; }
    public IReadOnlyList<BackupConfig> Configs { get; init; } = Array.Empty<BackupConfig>();
}

public sealed class PluginConfigAugmentationItem
{
    public string ConfigId { get; init; } = string.Empty;
    public IReadOnlyList<ManagedFolder> FoldersToAdd { get; init; } = Array.Empty<ManagedFolder>();
}

public sealed class PluginConfigAugmentationResult
{
    public bool Handled { get; init; }
    public IReadOnlyList<PluginConfigAugmentationItem> Items { get; init; } = Array.Empty<PluginConfigAugmentationItem>();
}

public sealed class PluginSettingsSaveResult
{
    public IReadOnlyDictionary<string, string> PreviousSettings { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> CurrentSettings { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class PluginConfigAugmentationRunResult
{
    public int AddedFolderCount { get; init; }
    public int UpdatedConfigCount { get; init; }
    public IReadOnlyList<string> TouchedPluginIds { get; init; } = Array.Empty<string>();
}
