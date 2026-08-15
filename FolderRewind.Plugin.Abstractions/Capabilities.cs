using System.Text.Json;

namespace FolderRewind.Plugin.Abstractions;

public interface IDiscoveryCapability : IPluginCapability
{
    DiscoveryProviderId ProviderId { get; }
    ValueTask<DiscoveryResult> DiscoverAsync(DiscoveryRequest request, PluginInvocationContext context);
}

public sealed record DiscoveryRequest(IReadOnlyList<string> UserRoots);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveryCandidate> Candidates, IReadOnlyList<PluginDiagnostic> Diagnostics);
public sealed record DiscoveryCandidate(string CandidateId, string DisplayName, IReadOnlyList<ConfigDraft> ConfigDrafts);

public interface IConfigReconciliationCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<ConfigChangeProposal?> ProposeAsync(ConfigReconciliationRequest request, PluginInvocationContext context);
}

public sealed record ConfigReconciliationRequest(ConfigSnapshot Config, string Reason);
public sealed record ConfigChangeProposal(
    string ProposalId,
    string ConfigId,
    ConfigRevision ExpectedRevision,
    string Reason,
    IReadOnlyList<ConfigChange> Changes,
    IReadOnlyList<PluginDiagnostic> Diagnostics);

public enum ConfigChangeImpact
{
    Additive = 0,
    Mutating = 1,
    Destructive = 2
}

public enum ConfigFieldOwnership
{
    Provider = 0,
    User = 1
}

public abstract record ConfigChange(ConfigChangeImpact Impact, ConfigFieldOwnership Ownership);
public sealed record AddFolderChange(FolderDraft Folder)
    : ConfigChange(ConfigChangeImpact.Additive, ConfigFieldOwnership.Provider);
public sealed record UpdateFolderChange(Guid FolderId, string? Path, string? DisplayName)
    : ConfigChange(ConfigChangeImpact.Mutating, ConfigFieldOwnership.User);
public sealed record RemoveFolderChange(Guid FolderId)
    : ConfigChange(ConfigChangeImpact.Destructive, ConfigFieldOwnership.User);
public sealed record SetProviderOptionsChange(StateOwnerId StateOwnerId, int SchemaVersion, JsonElement Options)
    : ConfigChange(ConfigChangeImpact.Mutating, ConfigFieldOwnership.Provider);
public sealed record SetArtifactTransformPolicyChange(ArtifactTransformerId? TransformerId)
    : ConfigChange(ConfigChangeImpact.Mutating, ConfigFieldOwnership.User);
public sealed record SetUserPolicyChange(string PolicyId, JsonElement Value)
    : ConfigChange(ConfigChangeImpact.Mutating, ConfigFieldOwnership.User);

public sealed record ConfigDraft(
    ConfigKindRef Kind,
    string SuggestedName,
    IReadOnlyList<FolderDraft> Folders,
    IReadOnlyDictionary<StateOwnerId, ProviderStateDraft> ProviderStates);
public sealed record FolderDraft(
    string Path,
    string DisplayName,
    IReadOnlyDictionary<StateOwnerId, ProviderStateDraft> ProviderStates);
public sealed record ProviderStateDraft(StateOwnerId StateOwnerId, int SchemaVersion, JsonElement Data);

public interface IFilePolicyCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<FilePolicyResult> ResolveAsync(FilePolicyRequest request, PluginInvocationContext context);
}

public sealed record FilePolicyRequest(ConfigSnapshot Config, FolderSnapshot Folder);
public sealed record FilePolicyResult(IReadOnlyList<string> RequiredExclusions, IReadOnlyList<string> RequiredInclusions, IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IBackupScopeCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    IReadOnlyList<BackupScopeDescriptor> Scopes { get; }
    ValueTask<BackupScopeResult> ResolveAsync(BackupScopeRequest request, PluginInvocationContext context);
}

public sealed record BackupScopeDescriptor(BackupScopeId Id, string DisplayName, JsonElement FormSchema);
public sealed record BackupScopeRequest(ConfigSnapshot Config, FolderSnapshot Folder, BackupScopeId ScopeId, IReadOnlyDictionary<string, JsonElement> Parameters);
public sealed record BackupScopeResult(OperationReadiness Readiness, IReadOnlyList<string> IncludePatterns, IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IBackupConsistencyCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<IConsistencyLease> AcquireAsync(BackupConsistencyRequest request, PluginInvocationContext context);
}

public sealed record BackupConsistencyRequest(ConfigSnapshot Config, FolderSnapshot Folder, ConsistencyIntent Intent);
public interface IConsistencyLease : IAsyncDisposable
{
    string SourcePath { get; }
    IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
}

public interface IFolderMetadataCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<FolderMetadataResult> ReadAsync(FolderMetadataRequest request, PluginInvocationContext context);
}

public sealed record FolderMetadataRequest(ConfigSnapshot Config, FolderSnapshot Folder);
public sealed record FolderMetadataResult(IReadOnlyDictionary<string, string> Values, IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IRestoreCoordinatorCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<RestoreCoordinatorResult> CoordinateAsync(RestoreCoordinatorRequest request, PluginInvocationContext context);
}

public delegate ValueTask<OperationOutcome> RestoreMutationContinuation(CancellationToken cancellationToken);
public sealed record RestoreCoordinatorRequest(ConfigSnapshot Config, FolderSnapshot Folder, string HistoryItemId, RestoreMutationContinuation ContinueMutationAsync);
public sealed record RestoreCoordinatorResult(OperationOutcome Outcome, IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IPluginCommandCapability : IPluginCapability
{
    IReadOnlyList<PluginCommandDescriptor> Commands { get; }
    ValueTask<PluginCommandResult> ExecuteAsync(PluginCommandRequest request, PluginInvocationContext context);
}

public sealed record PluginCommandDescriptor(PluginCommandId Id, string DisplayName, JsonElement ArgumentSchema)
{
    public string? DefaultHotkey { get; init; }
    public bool IsGlobalHotkey { get; init; }
}
public sealed record PluginCommandRequest(PluginCommandId Id, IReadOnlyDictionary<string, JsonElement> Arguments);
public sealed record PluginCommandResult(OperationOutcome Outcome, IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IKnotLinkIntegrationCapability : IPluginCapability
{
    IReadOnlyList<KnotLinkCommandDescriptor> Commands { get; }
    ValueTask<PluginCommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, string> arguments, PluginInvocationContext context);
}

public sealed record KnotLinkCommandDescriptor(string Command, string Description)
{
    /// <summary>
    /// Only route the command when every declared argument matches. This lets a plugin
    /// extend a Core command such as BACKUP for a semantic selector without taking over
    /// unrelated BACKUP requests. Boolean values use KnotLink's semantic aliases.
    /// </summary>
    public IReadOnlyDictionary<string, string> RequiredArguments { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public interface IProviderStateMigrationCapability : IPluginCapability
{
    StateOwnerId StateOwnerId { get; }
    int CurrentSchemaVersion { get; }
    ValueTask<ProviderStatePatch> MigrateAsync(ProviderStateSnapshot state, PluginInvocationContext context);
}
