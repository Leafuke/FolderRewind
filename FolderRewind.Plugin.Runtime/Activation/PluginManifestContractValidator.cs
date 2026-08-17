using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

public static class PluginManifestContractValidator
{
    public static void ValidateStatic(PluginManifestContract manifest, PluginId expectedPluginId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.PluginId != expectedPluginId)
        {
            throw new InvalidDataException("Manifest PluginId does not match the installed plugin identity.");
        }
        if (!manifest.RequiredApi.IsSatisfiedBy(PluginApiVersion.HostVersion))
        {
            throw new InvalidDataException("Manifest requires an incompatible Plugin API version.");
        }
        RequireUnique(manifest.RequestedHostServices, "requested Host service");
        RequireUnique(manifest.Capabilities, "capability");
        RequireUnique(manifest.ConfigKinds.Select(value => value.Kind), "Config Kind");
        RequireUnique(manifest.ArtifactFormats.Select(value => value.Format), "Artifact Format");
        RequireUnique(manifest.ArtifactTransformers.Select(value => value.TransformerId), "Artifact Transformer");
        RequireUnique(manifest.RestoreStrategies.Select(value => value.RestoreStrategyId), "Restore Strategy");

        if (manifest.ConfigKinds.Any(value =>
                !StringComparer.Ordinal.Equals(value.Kind.OwnerId.Value, expectedPluginId.Value)))
        {
            throw new InvalidDataException("Plugin Config Kinds must be owned by the declaring PluginId.");
        }

        foreach (var format in manifest.ArtifactFormats)
        {
            if (!StringComparer.Ordinal.Equals(format.Format.OwnerId.Value, expectedPluginId.Value)
                || format.MinimumVersion < 0
                || format.MaximumVersion < format.MinimumVersion)
            {
                throw new InvalidDataException("Plugin Artifact Formats require the plugin owner and a valid version range.");
            }
        }
        foreach (var transformer in manifest.ArtifactTransformers)
        {
            if (transformer.TransformerId.PluginId != expectedPluginId
                || transformer.CompatibleConfigKinds.Count == 0
                || transformer.CompatibleCoreModes.Count == 0
                || transformer.CompatibleCompleteness.Count == 0
                || transformer.SupportedFailureBehaviors.Count == 0
                || transformer.ParameterSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                throw new InvalidDataException("Artifact Transformer declaration is incomplete or owned by another plugin.");
            }
            RequireUnique(transformer.CompatibleConfigKinds, "compatible Config Kind");
            RequireUnique(transformer.CompatibleCoreModes, "compatible Core mode");
            RequireUnique(transformer.CompatibleCompleteness, "compatible completeness");
            RequireUnique(transformer.SupportedFailureBehaviors, "failure behavior");
        }
        foreach (var strategy in manifest.RestoreStrategies)
        {
            if (strategy.RestoreStrategyId.PluginId != expectedPluginId
                || strategy.SupportedFormats.Count == 0
                || strategy.SupportedCompleteness.Count == 0
                || strategy.SupportedRestoreModes.Count == 0)
            {
                throw new InvalidDataException("Restore Strategy declaration is incomplete or owned by another plugin.");
            }
            foreach (var supported in strategy.SupportedFormats)
            {
                if (!StringComparer.Ordinal.Equals(supported.Format.OwnerId.Value, expectedPluginId.Value)
                    || supported.MinimumVersion < 0
                    || supported.MaximumVersion < supported.MinimumVersion)
                {
                    throw new InvalidDataException("Restore Strategy format range is invalid or owned by another plugin.");
                }
            }
            RequireUnique(strategy.SupportedFormats.Select(value => value.Format), "Restore Strategy Artifact Format");
            RequireUnique(strategy.SupportedCompleteness, "Restore Strategy completeness");
            RequireUnique(strategy.SupportedRestoreModes, "Restore Mode");
        }

        if (manifest.ArtifactTransformers.Count != 0)
        {
            RequireServices(manifest, HostServiceKind.ArtifactRead, HostServiceKind.ArtifactTransformStaging);
        }
        if (manifest.RestoreStrategies.Count != 0)
        {
            RequireServices(manifest, HostServiceKind.ArtifactRead, HostServiceKind.RestoreMaterializationWorkspace);
        }
    }

    public static void ValidateConfigKindInventory(
        PluginManifestContract candidate,
        IEnumerable<PluginManifestContract> installedManifests)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(installedManifests);
        ValidateStatic(candidate, candidate.PluginId);

        var occupied = new Dictionary<ConfigKindRef, PluginId>();
        foreach (var installed in installedManifests.Where(value => value.PluginId != candidate.PluginId))
        {
            ValidateStatic(installed, installed.PluginId);
            foreach (var declaration in installed.ConfigKinds)
            {
                if (!occupied.TryAdd(declaration.Kind, installed.PluginId))
                    throw new InvalidDataException($"Installed plugin inventory contains duplicate Config Kind '{declaration.Kind}'.");
            }
        }

        foreach (var declaration in candidate.ConfigKinds)
        {
            if (occupied.TryGetValue(declaration.Kind, out var owner))
            {
                throw new InvalidDataException(
                    $"Config Kind '{declaration.Kind}' is already declared by installed plugin '{owner}'.");
            }
        }
    }

    internal static void ValidateRuntime(
        PluginManifestContract manifest,
        CapabilityRegistrationSet registrations)
    {
        var runtimeKinds = registrations.Capabilities.SelectMany(GetKinds).Distinct().ToHashSet();
        if (!runtimeKinds.SetEquals(manifest.Capabilities))
        {
            throw new InvalidDataException("Runtime capability registrations do not match the static Manifest.");
        }
        var transformerIds = registrations.Capabilities
            .OfType<IBackupArtifactTransformerCapability>()
            .Select(value => value.TransformerId)
            .ToHashSet();
        if (!transformerIds.SetEquals(manifest.ArtifactTransformers.Select(value => value.TransformerId)))
        {
            throw new InvalidDataException("Runtime Artifact Transformers do not match the static Manifest.");
        }
        var strategyIds = registrations.Capabilities
            .OfType<IRestoreMaterializerCapability>()
            .Select(value => value.RestoreStrategyId)
            .ToHashSet();
        if (!strategyIds.SetEquals(manifest.RestoreStrategies.Select(value => value.RestoreStrategyId)))
        {
            throw new InvalidDataException("Runtime Restore Materializers do not match the static Manifest.");
        }
        if (registrations.Capabilities.OfType<IBackupCompletionObserverCapability>().Any()
            != manifest.HasBackupCompletionObserver)
        {
            throw new InvalidDataException("Runtime Completion Observer does not match the static Manifest.");
        }
    }

    private static IEnumerable<PluginCapabilityKind> GetKinds(IPluginCapability capability)
    {
        if (capability is IDiscoveryCapability) yield return PluginCapabilityKind.Discovery;
        if (capability is IConfigReconciliationCapability) yield return PluginCapabilityKind.ConfigReconciliation;
        if (capability is IFilePolicyCapability) yield return PluginCapabilityKind.FilePolicy;
        if (capability is IBackupScopeCapability) yield return PluginCapabilityKind.BackupScope;
        if (capability is IBackupConsistencyCapability) yield return PluginCapabilityKind.BackupConsistency;
        if (capability is IFolderMetadataCapability) yield return PluginCapabilityKind.FolderMetadata;
        if (capability is IRestoreCoordinatorCapability) yield return PluginCapabilityKind.RestoreCoordinator;
        if (capability is IPluginCommandCapability) yield return PluginCapabilityKind.PluginCommand;
        if (capability is IKnotLinkIntegrationCapability) yield return PluginCapabilityKind.KnotLinkIntegration;
        if (capability is IProviderStateMigrationCapability) yield return PluginCapabilityKind.ProviderStateMigration;
        if (capability is IBackupArtifactTransformerCapability) yield return PluginCapabilityKind.BackupArtifactTransformer;
        if (capability is IBackupCompletionObserverCapability) yield return PluginCapabilityKind.BackupCompletionObserver;
        if (capability is IRestoreMaterializerCapability) yield return PluginCapabilityKind.RestoreMaterializer;
    }

    private static void RequireServices(PluginManifestContract manifest, params HostServiceKind[] required)
    {
        var declared = manifest.RequestedHostServices.ToHashSet();
        if (required.Any(service => !declared.Contains(service)))
        {
            throw new InvalidDataException("Manifest omits a high-impact Host service required by its Artifact capabilities.");
        }
    }

    private static void RequireUnique<T>(IEnumerable<T> values, string name)
        where T : notnull
    {
        var materialized = values.ToArray();
        if (materialized.Length != materialized.Distinct().Count())
        {
            throw new InvalidDataException($"Manifest contains duplicate {name} declarations.");
        }
    }
}
