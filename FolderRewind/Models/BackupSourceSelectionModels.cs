using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace FolderRewind.Models;

public enum BackupSourceSelectionMode
{
    All = 0,
    Include = 1
}

public sealed class BackupSourceSelection
{
    public BackupSourceSelectionMode Mode { get; set; } = BackupSourceSelectionMode.All;
    public ObservableCollection<string> IncludePatterns { get; set; } = new();
    public ObservableCollection<string> ResourceIds { get; set; } = new();

    [JsonIgnore]
    public bool IsPartial => Mode == BackupSourceSelectionMode.Include;
}

public enum BackupHistoryMode
{
    PerSource = 0,
    GroupedRun = 1
}

public sealed class DiscoveryOrigin
{
    public string ProviderId { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public Dictionary<string, string> ExternalIds { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<string> ResourceIds { get; set; } = new();
    public string PresetShareId { get; set; } = string.Empty;
    public string PresetVersion { get; set; } = string.Empty;
    public string ManifestRevision { get; set; } = string.Empty;
}
