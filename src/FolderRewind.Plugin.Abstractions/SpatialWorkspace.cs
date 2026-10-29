namespace FolderRewind.Plugin.Abstractions;

/// <summary>Explicit Host-owned immutable input. RootPath is a leased read-only tree, never the live target.</summary>
public sealed record SpatialFixedInput(string Identity, string RootPath, string ContentDigest)
{
    public ISpatialInputCoverage? Coverage { get; init; }
}

public enum SpatialPathPresence { Unknown = 0, Present = 1, Absent = 2, Filtered = 3 }
public interface ISpatialInputCoverage
{
    SpatialPathPresence InspectPath(string relativePath);
}

/// <summary>Opt-in: the preview must consume FixedInput and must never fall back to Folder.Path.</summary>
public interface IFixedSpatialPreviewCapability : ISpatialPreviewCapability { }

public sealed record SpatialWorld(string Id, LocalizedText Name);
/// <summary>Provider-owned coordinate space. Null bounds means the whole layer/world, not an empty selection.</summary>
public sealed record SpatialSubjectLocation(string WorldId, string? LayerId, SpatialPreviewBounds? Bounds);

public enum SpatialChangeKind { Added = 0, Deleted = 1, Modified = 2, ContainerOnly = 3, Unavailable = 4 }
public sealed record SpatialComparisonCategory(string Id, LocalizedText Name, bool DefaultVisible);
public sealed record SpatialComparisonDescription(IReadOnlyList<SpatialComparisonCategory> Categories);
public sealed record SpatialChange(string Id, LocalizedText Name, SpatialChangeKind Kind,
    SpatialSubjectLocation? Location, IReadOnlyList<string> Components, LocalizedText Description)
{
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
}
public sealed record SpatialComparisonRequest(Guid ComparisonId, SpatialPreviewSource Left, SpatialPreviewSource Right,
    string? Cursor, int Count)
{
    public SpatialSubjectLocation? InitialPriority { get; init; }
}
public sealed record SpatialComparisonPage(IReadOnlyList<SpatialChange> Changes, string? NextCursor, long ScannedFiles);

/// <summary>Optional read-only comparison. Independent of Merge and Artifact representations.</summary>
public interface ISpatialComparisonCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<SpatialComparisonDescription> DescribeComparisonAsync(SpatialComparisonRequest request, PluginInvocationContext context)
        => ValueTask.FromResult(new SpatialComparisonDescription(Array.Empty<SpatialComparisonCategory>()));
    ValueTask<SpatialComparisonPage> CompareAsync(SpatialComparisonRequest request, PluginInvocationContext context);
    ValueTask CloseComparisonAsync(Guid comparisonId, PluginInvocationContext context);
}

public sealed record SpatialScopeSelection(string WorldId, string LayerId, SpatialPreviewBounds Bounds);
public sealed record SpatialScopeRequest(SpatialPreviewSource Source, IReadOnlyList<SpatialScopeSelection> Selections);
public sealed record SpatialScopeResult(BackupScopeId ScopeId, IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<SpatialScopeSelection> EffectiveSelections, IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<PluginDiagnostic> Diagnostics);

/// <summary>Maps coordinates to existing BackupScope parameters. The Host owns configuration changes.</summary>
public interface ISpatialScopeSelectionCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<SpatialScopeResult> ResolveSelectionAsync(SpatialScopeRequest request, PluginInvocationContext context);
}
