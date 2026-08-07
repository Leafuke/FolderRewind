using System;
using System.Collections.Generic;

namespace FolderRewind.Models;

public sealed class LudusaviCompiledIndex
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourceSha256 { get; init; } = string.Empty;
    public DateTime CompiledAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<LudusaviCompiledGame> Games { get; init; }
        = Array.Empty<LudusaviCompiledGame>();
}

public sealed class LudusaviCompiledGame
{
    public required string DefinitionId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> InstallDirectoryHints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LudusaviCompiledResource> Files { get; init; }
        = Array.Empty<LudusaviCompiledResource>();
    public IReadOnlyList<LudusaviCompiledResource> Registry { get; init; }
        = Array.Empty<LudusaviCompiledResource>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> NativeCloud { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class LudusaviCompiledResource
{
    public required string ResourceId { get; init; }
    public required BackupResourceKind Kind { get; init; }
    public required string Expression { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LudusaviCompiledConstraint> Constraints { get; init; }
        = Array.Empty<LudusaviCompiledConstraint>();
    public bool IsDisabled { get; init; }
}

public sealed class LudusaviCompiledConstraint
{
    public IReadOnlyList<string> OperatingSystems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Stores { get; init; } = Array.Empty<string>();
}

public sealed class FolderRewindGameOverrideDocument
{
    public const string CurrentMagic = "FolderRewindGameOverride";
    public const string CurrentSchemaVersion = "1.0";

    public string Magic { get; init; } = CurrentMagic;
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<FolderRewindGameOverrideEntry> Entries { get; init; }
        = Array.Empty<FolderRewindGameOverrideEntry>();
}

public sealed class FolderRewindGameOverrideEntry
{
    public string DefinitionId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<FolderRewindGameOverrideOperation> Operations { get; init; }
        = Array.Empty<FolderRewindGameOverrideOperation>();
}

public enum FolderRewindGameOverrideAction
{
    Add = 0,
    Remove = 1,
    Replace = 2,
    Disable = 3
}

public sealed class FolderRewindGameOverrideOperation
{
    public FolderRewindGameOverrideAction Action { get; init; }
    public BackupResourceKind Kind { get; init; }
    public string ResourceId { get; init; } = string.Empty;
    public string Expression { get; init; } = string.Empty;
    public string ReplacementExpression { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

public sealed class LudusaviManifestCacheMetadata
{
    public string GenerationId { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string SourceUri { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class LudusaviManifestPointer
{
    public string GenerationId { get; init; } = string.Empty;
}

public enum LudusaviManifestUpdateStatus
{
    Updated = 0,
    NotModified = 1
}

public sealed class LudusaviManifestUpdateResult
{
    public required LudusaviManifestUpdateStatus Status { get; init; }
    public required LudusaviManifestCacheMetadata Metadata { get; init; }
    public required LudusaviCompiledIndex Index { get; init; }
}
