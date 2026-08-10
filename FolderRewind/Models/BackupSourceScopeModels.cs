using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace FolderRewind.Models;

public enum BackupSourceScopeMode
{
    All = 0,
    Include = 1
}

public sealed class BackupSourceScope
{
    public BackupSourceScopeMode Mode { get; set; } = BackupSourceScopeMode.All;
    public ObservableCollection<string> IncludePatterns { get; set; } = new();

    [JsonIgnore]
    public bool IsPartial => Mode == BackupSourceScopeMode.Include;
}

public sealed class DiscoverySetIdentity
{
    public string ProviderId { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public string SetId { get; set; } = string.Empty;
    public Dictionary<string, string> ExternalIds { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public bool HasSameStableIdentity(DiscoverySetIdentity? other) => other != null
        && string.Equals(ProviderId, other.ProviderId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DefinitionId, other.DefinitionId, StringComparison.Ordinal)
        && string.Equals(SetId, other.SetId, StringComparison.Ordinal);

    public IReadOnlySet<string> ExternalIdentityKeys() => ExternalIds
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
        .SelectMany(pair => pair.Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => $"{pair.Key.Trim().ToLowerInvariant()}:{value.ToLowerInvariant()}"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReviewedDiscoveryBaseline
{
    public ObservableCollection<ReviewedDiscoverySource> Sources { get; set; } = new();
    public ObservableCollection<ReviewedDiscoveryOverride> UserOverrides { get; set; } = new();
}

public sealed class ReviewedDiscoverySource
{
    public string NormalizedRootPath { get; set; } = string.Empty;
    public BackupSourceScopeMode Mode { get; set; }
    public ObservableCollection<string> IncludePatterns { get; set; } = new();
    public ObservableCollection<string> ResourceIds { get; set; } = new();
}

public sealed class ReviewedDiscoveryOverride
{
    public string NormalizedRootPath { get; set; } = string.Empty;
    public string UpstreamFingerprint { get; set; } = string.Empty;
    public string CurrentFingerprint { get; set; } = string.Empty;
}

public enum DiscoverySourceChangeKind
{
    Added = 0,
    Changed = 1,
    Removed = 2,
    UserModified = 3,
    Conflict = 4
}

public sealed class DiscoverySourceChange
{
    public string NormalizedRootPath { get; init; } = string.Empty;
    public DiscoverySourceChangeKind Kind { get; init; }
    public ReviewedDiscoverySource? Previous { get; init; }
    public ReviewedDiscoverySource? Current { get; init; }
    public ReviewedDiscoverySource? Discovered { get; init; }
    public bool IsActionable { get; init; }
    public bool IsSelected { get; set; }
}

public sealed class DiscoveryOrigin
{
    public DiscoverySetIdentity Identity { get; set; } = new();
    public ReviewedDiscoveryBaseline ReviewedBaseline { get; set; } = new();
    public string PresetShareId { get; set; } = string.Empty;
    public string PresetVersion { get; set; } = string.Empty;
    public string ManifestRevision { get; set; } = string.Empty;
}
