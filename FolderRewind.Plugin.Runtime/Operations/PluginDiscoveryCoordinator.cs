using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Operations;

public sealed record DiscoveryDraftCommit(
    PluginId PluginId,
    DiscoveryProviderId ProviderId,
    IReadOnlyList<DiscoveryCandidate> Candidates);

/// <summary>
/// The Host implementation must assign Host identities and persist the entire
/// validated draft batch atomically, or leave configuration unchanged.
/// </summary>
public interface IDiscoveryDraftStore
{
    ValueTask CommitAsync(DiscoveryDraftCommit commit, CancellationToken cancellationToken);
}

public sealed record PluginDiscoveryRunResult(
    DiscoveryResult Discovery,
    bool DraftsCommitted);

public sealed class PluginDiscoveryCoordinator
{
    private readonly PluginRuntimeManager _runtime;
    private readonly IDiscoveryDraftStore _store;

    public PluginDiscoveryCoordinator(PluginRuntimeManager runtime, IDiscoveryDraftStore store)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<PluginDiscoveryRunResult> DiscoverAsync(
        PluginId pluginId,
        DiscoveryRequest request,
        bool autoCreateConfigs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.UserRoots);
        using var lease = _runtime.TryAcquire<IDiscoveryCapability>(pluginId, cancellationToken)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no active Discovery capability.");

        var discovery = await lease.Capability.DiscoverAsync(request, lease.Context).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Discovery capability returned null.");
        var validated = ValidateAndClone(discovery);
        if (!autoCreateConfigs || validated.Candidates.Count == 0)
        {
            return new PluginDiscoveryRunResult(validated, DraftsCommitted: false);
        }

        await _store.CommitAsync(
            new DiscoveryDraftCommit(pluginId, lease.Capability.ProviderId, validated.Candidates),
            cancellationToken).ConfigureAwait(false);
        return new PluginDiscoveryRunResult(validated, DraftsCommitted: true);
    }

    private static DiscoveryResult ValidateAndClone(DiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery.Candidates);
        ArgumentNullException.ThrowIfNull(discovery.Diagnostics);
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = discovery.Candidates.Select(candidate =>
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (string.IsNullOrWhiteSpace(candidate.CandidateId)
                || !candidateIds.Add(candidate.CandidateId))
            {
                throw new InvalidOperationException("Discovery candidates require unique, non-empty identities.");
            }
            if (string.IsNullOrWhiteSpace(candidate.DisplayName))
            {
                throw new InvalidOperationException("Discovery candidates require display names.");
            }
            ArgumentNullException.ThrowIfNull(candidate.ConfigDrafts);
            var configs = candidate.ConfigDrafts.Select(CloneConfig).ToArray();
            if (configs.Length == 0)
            {
                throw new InvalidOperationException("Discovery candidates require at least one config draft.");
            }
            return candidate with { ConfigDrafts = configs };
        }).ToArray();

        return new DiscoveryResult(candidates, discovery.Diagnostics.ToArray());
    }

    private static ConfigDraft CloneConfig(ConfigDraft config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.SuggestedName))
        {
            throw new InvalidOperationException("Config drafts require a suggested name.");
        }
        ArgumentNullException.ThrowIfNull(config.Folders);
        var folders = config.Folders.Select(folder =>
        {
            ArgumentNullException.ThrowIfNull(folder);
            if (string.IsNullOrWhiteSpace(folder.Path) || !System.IO.Path.IsPathFullyQualified(folder.Path))
            {
                throw new InvalidOperationException("Folder drafts require a fully qualified path.");
            }
            if (string.IsNullOrWhiteSpace(folder.DisplayName))
            {
                throw new InvalidOperationException("Folder drafts require a display name.");
            }
            return folder with
            {
                Path = System.IO.Path.GetFullPath(folder.Path),
                ProviderStates = CloneStates(folder.ProviderStates)
            };
        }).ToArray();
        if (folders.Length == 0)
        {
            throw new InvalidOperationException("Config drafts require at least one folder draft.");
        }

        return config with
        {
            Folders = folders,
            ProviderStates = CloneStates(config.ProviderStates)
        };
    }

    private static IReadOnlyDictionary<StateOwnerId, ProviderStateDraft> CloneStates(
        IReadOnlyDictionary<StateOwnerId, ProviderStateDraft> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var clone = new Dictionary<StateOwnerId, ProviderStateDraft>();
        foreach (var (owner, state) in states)
        {
            if (owner != state.StateOwnerId || state.SchemaVersion < 0 || state.Data.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("Provider state draft has an invalid owner, schema, or payload.");
            }
            clone.Add(owner, state with { Data = state.Data.Clone() });
        }
        return clone;
    }
}
