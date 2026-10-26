using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Plugin.Abstractions;

/// <summary>A Merge provider is independent of Artifact transforms, restore formats and Config Kind ownership.</summary>
public readonly record struct MergeProviderId
{
    [JsonConstructor]
    public MergeProviderId(PluginId pluginId, string providerId)
    {
        if (string.IsNullOrWhiteSpace(pluginId.Value)) throw new ArgumentException("PluginId is required.", nameof(pluginId));
        PluginId = pluginId;
        ProviderId = PluginIdentitySyntax.RequireLocal(providerId, nameof(providerId));
    }
    public PluginId PluginId { get; }
    public string ProviderId { get; }
    public override string ToString() => $"{PluginId}/{ProviderId}";
}

public sealed record MergePolicyDeclaration(string PolicyId, string Version, LocalizedText DisplayName, LocalizedText Description);
public sealed record MergeProviderDeclaration(MergeProviderId ProviderId, int ContractVersion,
    LocalizedText DisplayName, IReadOnlyList<ConfigKindRef> CompatibleConfigKinds, IReadOnlyList<MergePolicyDeclaration> Policies);

public enum MergeInputSide { Base = 0, Ours = 1, Theirs = 2 }
public enum MergeDecisionKind { Automatic = 0, Ours = 1, Theirs = 2, Base = 3, Delete = 4, Manual = 5 }
public enum MergeBoundaryScope { All = 0, Include = 1 }
public enum MergeBoundaryFilter { Blacklist = 0, Whitelist = 1 }

/// <summary>Exact inputs are complete relative to this boundary, not necessarily to a physical source directory.</summary>
public sealed record MergeInputBoundary(int SchemaVersion, MergeBoundaryScope Scope, IReadOnlyList<string> ScopeRules,
    MergeBoundaryFilter Filter, IReadOnlyList<string> FilterRules, bool UseRegex, string Fingerprint)
{
    public IReadOnlyList<PathFilterRule> TypedFilterRules { get; init; } = Array.Empty<PathFilterRule>();
}

/// <summary>Digest is a Host-defined content identity, not necessarily a raw SHA-256 digest.</summary>
public sealed record MergeInputEntry(string RelativePath, bool IsDirectory, long Length, string Digest);
public sealed record MergeInputPage(IReadOnlyList<MergeInputEntry> Entries, string? NextCursor);

/// <summary>Fixed, authorized input. Streams and this view expire when the enclosing invocation ends.</summary>
public interface IMergeSourceView
{
    ValueTask<MergeInputPage> ReadPageAsync(string? cursor, int count, CancellationToken cancellationToken);
    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    /// <summary>Absent is only returned when this fixed historical view proves coverage of the path.</summary>
    ValueTask<MergePathCoverage> InspectPathAsync(string relativePath, CancellationToken cancellationToken)
        => ValueTask.FromResult(new MergePathCoverage(relativePath, MergePathPresence.Unknown, null, []));
    /// <summary>Prove all possible paths in a required family are covered, or return filtered/unknown evidence.</summary>
    ValueTask<MergeCoverageResult> CheckCoverageAsync(MergeCoverageQuery query, CancellationToken cancellationToken)
        => ValueTask.FromResult(new MergeCoverageResult(query.RequirementId, string.Empty, MergeCoverageState.Unknown, []));
}

public sealed record MergeVersionInput(string VersionId, string RepresentationId, string TreeDigest,
    MergeInputBoundary Boundary, IMergeSourceView Files);

/// <summary>Host limits; providers may reject earlier but cannot raise these limits.</summary>
public sealed record MergeLimits(int MaximumBatchSize, int MaximumSubjects, int MaximumRecordBytes, long MaximumPlanBytes)
{
    public static MergeLimits Default { get; } = new(500, 1_000_000, 64 * 1024, 256L * 1024 * 1024);
}

public sealed record MergeAnalysisRequest(Guid SessionId, Guid PlanRevision, Guid SourceId, ConfigKindRef Kind,
    string PolicyId, string PolicyVersion, MergeVersionInput Base, MergeVersionInput Ours, MergeVersionInput Theirs, MergeLimits Limits);

/// <summary>One exclusive output owner. Its subjects may share files; different groups cannot share owned paths.</summary>
public sealed record MergeOutputGroup(string GroupId, LocalizedText DisplayName, JsonElement State);

/// <summary>InputIdentity is provider evidence in addition to the Host's fixed input fingerprint.</summary>
public sealed record MergeSubject(string SubjectId, string GroupId, string InputIdentity, LocalizedText DisplayName,
    LocalizedText Description, IReadOnlyList<MergeDecisionKind> AllowedDecisions, bool IsAutomatic, JsonElement State)
{
    /// <summary>A persisted, unresolvable analysis issue. Other groups can still be analyzed; Apply remains blocked.</summary>
    public bool IsBlocked { get; init; }
    /// <summary>Explicit opt-in. Automatic remains the default; permitted overrides are AllowedDecisions.</summary>
    public bool CanOverrideAutomatic { get; init; }
    public IReadOnlyList<SpatialSubjectLocation> SpatialLocations { get; init; } = Array.Empty<SpatialSubjectLocation>();
}

/// <summary>Host-owned analysis storage. A partial analysis is never an applicable plan.</summary>
public interface IMergePlanWriter
{
    ValueTask AddGroupAsync(MergeOutputGroup group, CancellationToken cancellationToken);
    ValueTask ClaimPathsAsync(string groupId, IReadOnlyList<string> inputPaths, IReadOnlyList<string> outputPaths, CancellationToken cancellationToken);
    ValueTask AddSubjectsAsync(IReadOnlyList<MergeSubject> subjects, CancellationToken cancellationToken);
    ValueTask<Stream> OpenDataWriteAsync(string key, CancellationToken cancellationToken);
    /// <summary>Read only data written and closed in this analysis invocation; the Host verifies and owns its lifetime.</summary>
    ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken cancellationToken);
}

public sealed record MergeSubjectDecision(MergeSubject Subject, MergeDecisionKind Choice, string? ManualContentId);
public sealed record MergeDecisionPage(IReadOnlyList<MergeSubjectDecision> Decisions, string? NextCursor);

/// <summary>Only the fixed group's persisted plan and signed decisions are readable.</summary>
public interface IMergeDecisionReader
{
    ValueTask<MergeDecisionPage> ReadPageAsync(string? cursor, int count, CancellationToken cancellationToken);
    ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken cancellationToken);
    ValueTask<Stream> OpenManualReadAsync(string contentId, CancellationToken cancellationToken);
}

public sealed record MergeBuildRequest(MergeAnalysisRequest Analysis, MergeOutputGroup Group, IMergeDecisionReader Decisions);

/// <summary>Every claimed input needs one final disposition. New content can only use predeclared output paths.</summary>
public interface IMergeOutputWorkspace
{
    ValueTask ReuseAsync(string relativePath, MergeInputSide side, string sourceRelativePath, CancellationToken cancellationToken);
    ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken);
    /// <summary>Read a generated file after its writer closes. Reused inputs remain available through the fixed source views.</summary>
    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string relativePath, CancellationToken cancellationToken);
}

public sealed record MergeProviderResult(OperationOutcome Outcome, IReadOnlyList<PluginDiagnostic> Diagnostics);

/// <summary>Domain analysis and composition only. Host owns persistence, validation, History and target mutation.</summary>
public interface IMergeProviderCapability : IPluginCapability
{
    MergeProviderId ProviderId { get; }
    int ContractVersion { get; }
    ValueTask<MergeProviderResult> AnalyzeAsync(MergeAnalysisRequest request, IMergePlanWriter plan, PluginInvocationContext context);
    ValueTask<MergeProviderResult> BuildAsync(MergeBuildRequest request, IMergeOutputWorkspace output, PluginInvocationContext context);
}
