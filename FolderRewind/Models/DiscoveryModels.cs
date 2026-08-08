using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Models;

public enum DiscoveryRequestMode
{
    FullMachine = 0,
    PresetTargeted = 1,
    UserRoots = 2
}

public enum DiscoveryConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum GameStore
{
    Unknown = 0,
    Steam = 1,
    Gog = 2,
    Epic = 3,
    Standalone = 4
}

public enum BackupResourceKind
{
    Directory = 0,
    FileSet = 1,
    Registry = 2
}

public enum BackupResourceSupportState
{
    Supported = 0,
    UnsupportedRegistry = 1,
    UnsupportedConstraint = 2,
    InvalidPath = 3
}

public enum DiscoveryDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed class DiscoveryProviderDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public int Priority { get; init; }
    public bool IsSpecialized { get; init; }
}

public sealed class DiscoveryRequest
{
    public DiscoveryRequestMode Mode { get; init; } = DiscoveryRequestMode.FullMachine;
    public IReadOnlyDictionary<GameStore, IReadOnlyList<string>> StoreRoots { get; init; }
        = new Dictionary<GameStore, IReadOnlyList<string>>();
    public IReadOnlyList<string> DisabledAutoRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UserRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DiscoveryDefinitionReference> Definitions { get; init; }
        = Array.Empty<DiscoveryDefinitionReference>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ProviderSettings { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DiscoveryDefinitionReference
{
    public required string ProviderId { get; init; }
    public required string DefinitionId { get; init; }
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DiscoveryProgress
{
    public required string ProviderId { get; init; }
    public required string Phase { get; init; }
    public string Message { get; init; } = string.Empty;
    public int Completed { get; init; }
    public int? Total { get; init; }
}

public sealed class DiscoveryDiagnostic
{
    public required DiscoveryDiagnosticSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public string DefinitionId { get; init; } = string.Empty;
}

public sealed class DiscoveryEvidence
{
    public DiscoveryConfidence Confidence { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public string Source { get; init; } = string.Empty;
}

public sealed class GameDefinition
{
    public required string ProviderId { get; init; }
    public required string DefinitionId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> NativeCloud { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameInstallation
{
    public required string InstallationId { get; init; }
    public GameStore Store { get; init; }
    public string StoreGameId { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public string LibraryRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> StoreUserIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DiscoveryEvidence> Evidence { get; init; } = Array.Empty<DiscoveryEvidence>();
}

public sealed class DetectedGameInstallation
{
    public GameStore Store { get; init; }
    public string StoreGameId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public string LibraryRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> StoreUserIds { get; init; } = Array.Empty<string>();
}

public sealed class BackupResourceCandidate
{
    public required string ResourceId { get; init; }
    public required string ProviderId { get; init; }
    public int ProviderPriority { get; init; }
    public bool IsSpecializedProvider { get; init; }
    public required string DisplayName { get; init; }
    public BackupResourceKind Kind { get; init; }
    public BackupResourceSupportState SupportState { get; init; } = BackupResourceSupportState.Supported;
    public string FixedRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();
    public string OriginalExpression { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Constraints { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DiscoveryEvidence> Evidence { get; init; } = Array.Empty<DiscoveryEvidence>();
    public bool FixedRootExists { get; init; }
    public bool IsSelectedByDefault { get; init; }
    public string SuppressedByProviderId { get; internal set; } = string.Empty;
    public string SuppressionReason { get; internal set; } = string.Empty;
    public bool IsSuppressed => !string.IsNullOrWhiteSpace(SuppressedByProviderId);

    public DiscoveryConfidence Confidence => Evidence.Count == 0
        ? DiscoveryConfidence.Low
        : Evidence.Max(item => item.Confidence);
}

public sealed class BackupSetCandidate
{
    public required string StableKey { get; init; }
    public required string DisplayName { get; init; }
    public string SuggestedConfigType { get; init; } = "Default";
    public IList<BackupResourceCandidate> Resources { get; init; } = new List<BackupResourceCandidate>();
}

public sealed class DiscoveredGameCandidate
{
    public required string StableKey { get; init; }
    public required GameDefinition Definition { get; init; }
    public IList<GameInstallation> Installations { get; init; } = new List<GameInstallation>();
    public IList<BackupSetCandidate> BackupSets { get; init; } = new List<BackupSetCandidate>();
}

public sealed class DiscoveryScanStatistics
{
    public int DefinitionsConsidered { get; init; }
    public int InstallationsFound { get; init; }
    public int ResourcesFound { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class DiscoveryProviderResult
{
    public required string ProviderId { get; init; }
    public IReadOnlyList<DiscoveredGameCandidate> Candidates { get; init; }
        = Array.Empty<DiscoveredGameCandidate>();
    public IReadOnlyList<DiscoveryDiagnostic> Diagnostics { get; init; }
        = Array.Empty<DiscoveryDiagnostic>();
    public DiscoveryScanStatistics Statistics { get; init; } = new();
}

public sealed class GameDiscoveryResult
{
    public IReadOnlyList<DiscoveredGameCandidate> Candidates { get; init; }
        = Array.Empty<DiscoveredGameCandidate>();
    public IReadOnlyList<DiscoveryDiagnostic> Diagnostics { get; init; }
        = Array.Empty<DiscoveryDiagnostic>();
}
