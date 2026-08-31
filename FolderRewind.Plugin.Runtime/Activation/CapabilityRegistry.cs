using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

internal readonly record struct CapabilityKey(string Kind, string Identity)
{
    public override string ToString() => $"{Kind}:{Identity}";
}

internal sealed record CapabilityRegistration(
    CapabilityKey Key,
    PluginId PluginId,
    IPluginCapability Capability);

internal sealed class CapabilityRegistrationSet
{
    private CapabilityRegistrationSet(
        PluginId pluginId,
        IReadOnlyList<IPluginCapability> capabilities,
        IReadOnlyList<CapabilityRegistration> registrations)
    {
        PluginId = pluginId;
        Capabilities = capabilities;
        Registrations = registrations;
    }

    public PluginId PluginId { get; }
    public IReadOnlyList<IPluginCapability> Capabilities { get; }
    public IReadOnlyList<CapabilityRegistration> Registrations { get; }

    public static CapabilityRegistrationSet Create(PluginId pluginId, IReadOnlyList<IPluginCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        EnsureAtMostOne<IDiscoveryCapability>("discovery");
        EnsureAtMostOne<IPluginCommandCapability>("plugin command");
        EnsureAtMostOne<IKnotLinkIntegrationCapability>("KnotLink integration");
        EnsureAtMostOne<IBackupCompletionObserverCapability>("backup completion observer");

        var registrations = new List<CapabilityRegistration>();
        foreach (var capability in capabilities)
        {
            var recognized = false;
            if (capability is IDiscoveryCapability discovery)
            {
                Add("discovery", discovery.ProviderId.Value, capability);
                recognized = true;
            }

            if (capability is IConfigReconciliationCapability reconciliation)
            {
                Add("config-reconciliation", reconciliation.Kind.ToString(), capability);
                recognized = true;
            }

            if (capability is IFilePolicyCapability filePolicy)
            {
                Add("file-policy", filePolicy.Kind.ToString(), capability);
                recognized = true;
            }

            if (capability is IBackupScopeCapability backupScope)
            {
                Add("backup-scope", backupScope.Kind.ToString(), capability);
                foreach (var scope in backupScope.Scopes ?? throw new InvalidOperationException("Backup scopes cannot be null."))
                {
                    Add("backup-scope-id", scope.Id.ToString(), capability);
                }
                recognized = true;
            }

            if (capability is IBackupConsistencyCapability consistency)
            {
                Add("backup-consistency", consistency.Kind.ToString(), capability);
                recognized = true;
            }

            if (capability is IFolderMetadataCapability metadata)
            {
                Add("folder-metadata", metadata.Kind.ToString(), capability);
                recognized = true;
            }

            if (capability is IVersionMetadataProviderCapability versionMetadata)
            {
                Add("version-metadata", versionMetadata.Kind.ToString(), capability);
                recognized = true;
            }

            if (capability is IRestoreCoordinatorCapability restore)
            {
                Add("restore-coordinator", restore.Kind.ToString(), capability);
                recognized = true;
            }

            if (capability is IProviderStateMigrationCapability migration)
            {
                Add("state-migration", migration.StateOwnerId.Value, capability);
                recognized = true;
            }

            if (capability is IPluginCommandCapability commands)
            {
                foreach (var command in commands.Commands ?? throw new InvalidOperationException("Plugin commands cannot be null."))
                {
                    Add("plugin-command", command.Id.ToString(), capability);
                }
                recognized = true;
            }

            if (capability is IKnotLinkIntegrationCapability knotLink)
            {
                foreach (var command in knotLink.Commands ?? throw new InvalidOperationException("KnotLink commands cannot be null."))
                {
                    if (string.IsNullOrWhiteSpace(command.Command))
                    {
                        throw new InvalidOperationException("KnotLink command identity cannot be empty.");
                    }

                    Add("knotlink-command", command.Command.Trim().ToUpperInvariant(), capability);
                }
                recognized = true;
            }

            if (capability is IBackupArtifactTransformerCapability transformer)
            {
                Add("artifact-transformer", transformer.TransformerId.ToString(), capability);
                recognized = true;
            }

            if (capability is IBackupCompletionObserverCapability)
            {
                Add("backup-completion-observer", pluginId.Value, capability);
                recognized = true;
            }

            if (capability is IRestoreMaterializerCapability materializer)
            {
                Add("restore-materializer", materializer.RestoreStrategyId.ToString(), capability);
                recognized = true;
            }

            if (!recognized)
            {
                throw new InvalidOperationException($"Unsupported capability type '{capability.GetType().FullName}'.");
            }
        }

        return new CapabilityRegistrationSet(pluginId, capabilities.ToArray(), registrations);

        void EnsureAtMostOne<TCapability>(string name)
            where TCapability : class, IPluginCapability
        {
            var count = capabilities.OfType<TCapability>().Count();
            if (count > 1)
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' may register at most one {name} capability instance, but registered {count}.");
            }
        }

        void Add(string kind, string identity, IPluginCapability capability)
        {
            var registration = new CapabilityRegistration(new CapabilityKey(kind, identity), pluginId, capability);
            if (registrations.Any(existing => existing.Key == registration.Key))
            {
                throw new InvalidOperationException($"Capability conflict inside plugin '{pluginId}': {registration.Key}.");
            }
            registrations.Add(registration);
        }
    }
}

internal sealed class CapabilityRegistry
{
    private readonly Dictionary<CapabilityKey, CapabilityRegistration> _registrations = new();

    public void Validate(CapabilityRegistrationSet candidate, PluginId? replacing = null)
    {
        foreach (var registration in candidate.Registrations)
        {
            if (_registrations.TryGetValue(registration.Key, out var current)
                && (!replacing.HasValue || current.PluginId != replacing.Value))
            {
                throw new InvalidOperationException(
                    $"Capability '{registration.Key}' is already owned by '{current.PluginId}'.");
            }
        }
    }

    public void Commit(CapabilityRegistrationSet candidate)
    {
        Remove(candidate.PluginId);
        foreach (var registration in candidate.Registrations)
        {
            _registrations.Add(registration.Key, registration);
        }
    }

    public void Remove(PluginId pluginId)
    {
        foreach (var key in _registrations
                     .Where(pair => pair.Value.PluginId == pluginId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _registrations.Remove(key);
        }
    }
}

internal sealed class TemporaryActivationContext : IPluginActivationContext
{
    private readonly List<IPluginCapability> _capabilities = new();

    public TemporaryActivationContext(
        PluginId pluginId,
        PluginSettingsSnapshot settings,
        IReadOnlyList<ConfigSnapshot> configs)
    {
        PluginId = pluginId;
        Settings = settings;
        Configs = configs;
    }

    public PluginId PluginId { get; }
    public PluginSettingsSnapshot Settings { get; }
    public IReadOnlyList<ConfigSnapshot> Configs { get; }
    public IReadOnlyList<IPluginCapability> Capabilities => _capabilities;

    public void RegisterCapability<TCapability>(TCapability capability)
        where TCapability : class, IPluginCapability
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (_capabilities.Any(existing => ReferenceEquals(existing, capability)))
        {
            throw new InvalidOperationException("The same capability instance cannot be registered twice.");
        }

        _capabilities.Add(capability);
    }
}
