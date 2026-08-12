using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Operations;

public sealed record ConfigReconciliationApplyPolicy(
    bool AllowAdditiveFolders,
    bool AllowProviderOptions)
{
    public static ConfigReconciliationApplyPolicy ReviewAll { get; } = new(false, false);
}

public sealed record ConfigChangeCommit(
    PluginId PluginId,
    string ConfigId,
    ConfigRevision ExpectedRevision,
    string ProposalId,
    IReadOnlyList<ConfigChange> Changes);

/// <summary>
/// Atomically verifies ExpectedRevision, applies every change, and returns the
/// new Host-issued revision. A failure must leave the configuration unchanged.
/// </summary>
public interface IConfigChangeStore
{
    ValueTask<ConfigRevision> CommitAsync(ConfigChangeCommit commit, CancellationToken cancellationToken);
}

public enum ConfigReconciliationStatus
{
    NoChanges = 0,
    ReviewRequired = 1,
    Committed = 2
}

public sealed record ConfigReconciliationRunResult(
    ConfigReconciliationStatus Status,
    ConfigChangeProposal? Proposal,
    ConfigRevision? CommittedRevision);

public sealed class ConfigReconciliationCoordinator
{
    private const int MaximumChanges = 256;
    private readonly PluginRuntimeManager _runtime;
    private readonly IConfigChangeStore _store;

    public ConfigReconciliationCoordinator(PluginRuntimeManager runtime, IConfigChangeStore store)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<ConfigReconciliationRunResult> ReconcileAsync(
        PluginId pluginId,
        ConfigReconciliationRequest request,
        ConfigReconciliationApplyPolicy applyPolicy,
        bool proposalConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Config);
        ArgumentNullException.ThrowIfNull(applyPolicy);

        using var lease = _runtime.TryAcquire<IConfigReconciliationCapability>(pluginId, cancellationToken)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no active Config Reconciliation capability.");
        if (lease.Capability.Kind != request.Config.Kind)
        {
            throw new InvalidOperationException("Config Reconciliation capability does not own the requested Config Kind.");
        }

        var proposed = await lease.Capability.ProposeAsync(request, lease.Context).ConfigureAwait(false);
        if (proposed is null)
        {
            return new ConfigReconciliationRunResult(ConfigReconciliationStatus.NoChanges, null, null);
        }

        var proposal = ValidateAndClone(pluginId, request.Config, proposed);
        if (proposal.Changes.Count == 0)
        {
            return new ConfigReconciliationRunResult(ConfigReconciliationStatus.NoChanges, proposal, null);
        }

        if (!proposalConfirmed && !CanAutoApply(proposal.Changes, applyPolicy))
        {
            return new ConfigReconciliationRunResult(ConfigReconciliationStatus.ReviewRequired, proposal, null);
        }

        var revision = await _store.CommitAsync(
            new ConfigChangeCommit(
                pluginId,
                proposal.ConfigId,
                proposal.ExpectedRevision,
                proposal.ProposalId,
                proposal.Changes),
            cancellationToken).ConfigureAwait(false);
        return new ConfigReconciliationRunResult(ConfigReconciliationStatus.Committed, proposal, revision);
    }

    private static ConfigChangeProposal ValidateAndClone(
        PluginId pluginId,
        ConfigSnapshot config,
        ConfigChangeProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.ProposalId))
        {
            throw new InvalidOperationException("Config Change Proposals require an identity.");
        }
        if (!StringComparer.Ordinal.Equals(proposal.ConfigId, config.ConfigId)
            || proposal.ExpectedRevision != config.Revision)
        {
            throw new InvalidOperationException("Config Change Proposal is stale or targets a different configuration.");
        }
        if (string.IsNullOrWhiteSpace(proposal.Reason))
        {
            throw new InvalidOperationException("Config Change Proposals require a user-visible reason.");
        }
        ArgumentNullException.ThrowIfNull(proposal.Changes);
        ArgumentNullException.ThrowIfNull(proposal.Diagnostics);
        if (proposal.Changes.Count > MaximumChanges)
        {
            throw new InvalidOperationException($"Config Change Proposal exceeds the {MaximumChanges}-change limit.");
        }

        var folderIds = config.Folders.Select(folder => folder.FolderId).ToHashSet();
        var touchedFolders = new HashSet<Guid>();
        var changes = proposal.Changes.Select(change => CloneChange(pluginId, folderIds, touchedFolders, change)).ToArray();
        var diagnostics = proposal.Diagnostics.Select(CloneDiagnostic).ToArray();
        return proposal with
        {
            ProposalId = proposal.ProposalId.Trim(),
            Reason = proposal.Reason.Trim(),
            Changes = changes,
            Diagnostics = diagnostics
        };
    }

    private static ConfigChange CloneChange(
        PluginId pluginId,
        IReadOnlySet<Guid> folderIds,
        ISet<Guid> touchedFolders,
        ConfigChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change switch
        {
            AddFolderChange add => new AddFolderChange(CloneFolder(add.Folder)),
            UpdateFolderChange update => CloneUpdate(folderIds, touchedFolders, update),
            RemoveFolderChange remove => CloneRemove(folderIds, touchedFolders, remove),
            SetProviderOptionsChange options => CloneProviderOptions(pluginId, options),
            SetArtifactTransformPolicyChange transform => new SetArtifactTransformPolicyChange(transform.TransformerId),
            SetUserPolicyChange policy => CloneUserPolicy(policy),
            _ => throw new InvalidOperationException($"Unsupported Config Change type '{change.GetType().FullName}'.")
        };
    }

    private static FolderDraft CloneFolder(FolderDraft folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (string.IsNullOrWhiteSpace(folder.Path) || !Path.IsPathFullyQualified(folder.Path))
        {
            throw new InvalidOperationException("Added folders require a fully qualified path.");
        }
        if (string.IsNullOrWhiteSpace(folder.DisplayName))
        {
            throw new InvalidOperationException("Added folders require a display name.");
        }

        var states = folder.ProviderStates.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Data = pair.Value.Data.Clone() });
        return folder with
        {
            Path = Path.GetFullPath(folder.Path),
            DisplayName = folder.DisplayName.Trim(),
            ProviderStates = states
        };
    }

    private static UpdateFolderChange CloneUpdate(
        IReadOnlySet<Guid> folderIds,
        ISet<Guid> touchedFolders,
        UpdateFolderChange change)
    {
        RequireExistingFolder(folderIds, touchedFolders, change.FolderId);
        if (change.Path is null && change.DisplayName is null)
        {
            throw new InvalidOperationException("Folder updates require at least one changed field.");
        }
        var path = change.Path;
        if (path is not null)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException("Updated folder paths must be fully qualified.");
            }
            path = Path.GetFullPath(path);
        }
        var displayName = change.DisplayName;
        if (displayName is not null)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new InvalidOperationException("Updated folder display names cannot be empty.");
            }
            displayName = displayName.Trim();
        }
        return change with { Path = path, DisplayName = displayName };
    }

    private static RemoveFolderChange CloneRemove(
        IReadOnlySet<Guid> folderIds,
        ISet<Guid> touchedFolders,
        RemoveFolderChange change)
    {
        RequireExistingFolder(folderIds, touchedFolders, change.FolderId);
        return change;
    }

    private static void RequireExistingFolder(
        IReadOnlySet<Guid> folderIds,
        ISet<Guid> touchedFolders,
        Guid folderId)
    {
        if (folderId == Guid.Empty || !folderIds.Contains(folderId))
        {
            throw new InvalidOperationException("Config Change targets an unknown FolderId.");
        }
        if (!touchedFolders.Add(folderId))
        {
            throw new InvalidOperationException("A proposal cannot update or remove the same folder more than once.");
        }
    }

    private static SetProviderOptionsChange CloneProviderOptions(
        PluginId pluginId,
        SetProviderOptionsChange change)
    {
        if (!StringComparer.Ordinal.Equals(change.StateOwnerId.Value, pluginId.Value))
        {
            throw new InvalidOperationException("Provider options can only be proposed by their StateOwner.");
        }
        if (change.SchemaVersion < 0 || change.Options.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Provider options require a non-negative schema and JSON payload.");
        }
        return change with { Options = change.Options.Clone() };
    }

    private static SetUserPolicyChange CloneUserPolicy(SetUserPolicyChange change)
    {
        if (string.IsNullOrWhiteSpace(change.PolicyId) || change.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("User policy changes require an identity and JSON value.");
        }
        return change with { PolicyId = change.PolicyId.Trim(), Value = change.Value.Clone() };
    }

    private static PluginDiagnostic CloneDiagnostic(PluginDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return diagnostic with
        {
            Arguments = diagnostic.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    private static bool CanAutoApply(
        IReadOnlyList<ConfigChange> changes,
        ConfigReconciliationApplyPolicy policy)
        => changes.All(change => change switch
        {
            AddFolderChange => policy.AllowAdditiveFolders,
            SetProviderOptionsChange => policy.AllowProviderOptions,
            _ => false
        });
}
