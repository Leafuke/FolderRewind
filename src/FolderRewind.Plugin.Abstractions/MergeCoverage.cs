namespace FolderRewind.Plugin.Abstractions;

public enum MergePathPresence { Unknown = 0, Present = 1, Absent = 2, Filtered = 3 }
public enum MergeCoverageState { Unknown = 0, Complete = 1, Filtered = 2 }

/// <summary>Host evidence; a possible excluded path is not a claim that that file physically existed.</summary>
public sealed record MergeCoverageEvidence(string Code, string? Rule, string? ExamplePath);
public sealed record MergePathCoverage(string RelativePath, MergePathPresence Presence, MergeInputEntry? Entry,
    IReadOnlyList<MergeCoverageEvidence> Evidence);

/// <summary>
/// A provider's required path family under a literal relative root (empty means Source root).
/// Patterns are case-insensitive relative globs: * and ? stay in a segment, ** crosses separators,
/// and **/ also matches zero directories. Character classes and parent traversal are unsupported.
/// The query includes possible absent paths, not just inventory entries. It grants no new read/write authority.
/// </summary>
public sealed record MergeCoverageQuery(string RequirementId, string RelativeRoot, IReadOnlyList<string> Patterns)
{
    /// <summary>Exact blacklist rules the provider declares irrelevant to these domain dependencies. This changes proof only, never input/output authority.</summary>
    public IReadOnlyList<PathFilterRule> IgnoredBlacklistRules { get; init; } = Array.Empty<PathFilterRule>();
}
public sealed record MergeCoverageResult(string RequirementId, string BoundaryFingerprint, MergeCoverageState State,
    IReadOnlyList<MergeCoverageEvidence> Evidence);
