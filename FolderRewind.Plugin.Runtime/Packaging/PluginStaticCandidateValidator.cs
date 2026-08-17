using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Settings;

namespace FolderRewind.Plugin.Runtime.Packaging;

public sealed record ReachableOwnedArtifactRequirement(
    ArtifactId ArtifactId,
    ArtifactFormatRef Format,
    int FormatVersion,
    ArtifactCompleteness Completeness,
    RestoreStrategyId RestoreStrategyId);

public sealed record PluginPackageInstallValidationFacts(
    PluginSettingsSnapshot PersistedSettings,
    IReadOnlyList<PluginManifestContract> InstalledManifests,
    IReadOnlyList<ReachableOwnedArtifactRequirement> ReachableOwnedArtifacts)
{
    public static PluginPackageInstallValidationFacts Empty(PluginId pluginId)
        => new(
            new PluginSettingsSnapshot(pluginId, new Dictionary<string, System.Text.Json.JsonElement>()),
            Array.Empty<PluginManifestContract>(),
            Array.Empty<ReachableOwnedArtifactRequirement>());
}

public static class PluginStaticCandidateValidator
{
    public static PluginSettingsSnapshot Validate(
        string candidateRoot,
        ParsedPluginPackageManifest parsed,
        PluginPackageInstallValidationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(facts);
        var root = Path.GetFullPath(candidateRoot);
        if (facts.PersistedSettings.PluginId != parsed.Contract.PluginId)
            throw new InvalidDataException("Persisted plugin settings do not match the candidate PluginId.");

        ValidateFacts(parsed, facts);

        var schemaPath = ResolveUnderRoot(root, parsed.Contract.SettingsSchema);
        var schema = PluginSettingsSchema.Parse(File.ReadAllBytes(schemaPath));
        var settings = schema.Validate(facts.PersistedSettings);
        if (!settings.IsValid)
        {
            throw new InvalidDataException(
                "Plugin candidate settings do not satisfy the declared settings schema: "
                + string.Join(",", settings.Issues.Select(issue => issue.Code)));
        }

        ValidateEntryAssembly(
            ResolveUnderRoot(root, parsed.Contract.EntryAssembly),
            parsed.Contract.EntryType,
            parsed.Architectures);
        return settings.NormalizedSettings;
    }

    public static void ValidateFacts(
        ParsedPluginPackageManifest parsed,
        PluginPackageInstallValidationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.PersistedSettings.PluginId != parsed.Contract.PluginId)
            throw new InvalidDataException("Persisted plugin settings do not match the candidate PluginId.");
        PluginManifestContractValidator.ValidateConfigKindInventory(
            parsed.Contract,
            facts.InstalledManifests);
        ValidateOwnedArtifacts(parsed.Contract, facts.ReachableOwnedArtifacts);
    }

    private static void ValidateOwnedArtifacts(
        PluginManifestContract candidate,
        IReadOnlyList<ReachableOwnedArtifactRequirement> requirements)
    {
        foreach (var artifact in requirements)
        {
            var format = candidate.ArtifactFormats.SingleOrDefault(value => value.Format == artifact.Format);
            if (format is null
                || artifact.FormatVersion < format.MinimumVersion
                || artifact.FormatVersion > format.MaximumVersion)
            {
                throw new InvalidDataException(
                    $"Candidate cannot read reachable Artifact format {artifact.Format} v{artifact.FormatVersion}.");
            }

            var strategy = candidate.RestoreStrategies.SingleOrDefault(value =>
                value.RestoreStrategyId == artifact.RestoreStrategyId);
            if (strategy is null
                || !strategy.SupportedCompleteness.Contains(artifact.Completeness)
                || !strategy.SupportedFormats.Any(value =>
                    value.Format == artifact.Format
                    && artifact.FormatVersion >= value.MinimumVersion
                    && artifact.FormatVersion <= value.MaximumVersion))
            {
                throw new InvalidDataException($"Candidate cannot materialize reachable Artifact {artifact.ArtifactId}.");
            }
        }
    }

    private static void ValidateEntryAssembly(
        string assemblyPath,
        string entryType,
        IReadOnlyList<string> declaredArchitectures)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            throw new InvalidDataException("Plugin entryAssembly must be a managed .NET assembly.");

        ValidateArchitecture(pe.PEHeaders.CoffHeader.Machine, declaredArchitectures);
        var metadata = pe.GetMetadataReader();
        var abstractions = metadata.AssemblyReferences
            .Select(metadata.GetAssemblyReference)
            .Where(value => StringComparer.Ordinal.Equals(
                metadata.GetString(value.Name),
                "FolderRewind.Plugin.Abstractions"))
            .ToArray();
        if (abstractions.Length != 1 || abstractions[0].Version.Major != PluginApiVersion.HostVersion.Major)
        {
            throw new InvalidDataException(
                "Plugin entryAssembly must reference FolderRewind.Plugin.Abstractions 3.x exactly once.");
        }

        var separator = entryType.LastIndexOf('.');
        var expectedNamespace = separator < 0 ? string.Empty : entryType[..separator];
        var expectedName = separator < 0 ? entryType : entryType[(separator + 1)..];
        TypeDefinition? matched = null;
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            if (StringComparer.Ordinal.Equals(metadata.GetString(definition.Namespace), expectedNamespace)
                && StringComparer.Ordinal.Equals(metadata.GetString(definition.Name), expectedName))
            {
                matched = definition;
                break;
            }
        }
        if (matched is null)
            throw new InvalidDataException($"Plugin entry type '{entryType}' was not found in entryAssembly metadata.");

        var type = matched.Value;
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility != TypeAttributes.Public || type.Attributes.HasFlag(TypeAttributes.Abstract))
            throw new InvalidDataException("Plugin entry type must be public and concrete.");

        var implementsContract = type.GetInterfaceImplementations().Any(handle =>
        {
            var implementation = metadata.GetInterfaceImplementation(handle);
            if (implementation.Interface.Kind != HandleKind.TypeReference) return false;
            var reference = metadata.GetTypeReference((TypeReferenceHandle)implementation.Interface);
            return StringComparer.Ordinal.Equals(metadata.GetString(reference.Namespace), "FolderRewind.Plugin.Abstractions")
                   && StringComparer.Ordinal.Equals(metadata.GetString(reference.Name), nameof(IFolderRewindPlugin));
        });
        if (!implementsContract)
            throw new InvalidDataException("Plugin entry type must implement IFolderRewindPlugin.");

        var hasConstructor = type.GetMethods().Any(handle =>
        {
            var method = metadata.GetMethodDefinition(handle);
            return StringComparer.Ordinal.Equals(metadata.GetString(method.Name), ".ctor")
                   && method.Attributes.HasFlag(MethodAttributes.Public)
                   && method.GetParameters().Count == 0;
        });
        if (!hasConstructor)
            throw new InvalidDataException("Plugin entry type requires a public parameterless constructor.");
    }

    private static void ValidateArchitecture(
        Machine machine,
        IReadOnlyList<string> declaredArchitectures)
    {
        if (declaredArchitectures.Contains("any", StringComparer.Ordinal)) return;
        var actual = machine switch
        {
            Machine.I386 => "x86",
            Machine.Amd64 => "x64",
            Machine.Arm64 => "arm64",
            _ => string.Empty
        };
        if (actual.Length == 0 || !declaredArchitectures.Contains(actual, StringComparer.Ordinal))
            throw new InvalidDataException("Plugin entryAssembly architecture does not match the Manifest declaration.");
    }

    private static string ResolveUnderRoot(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("Plugin candidate file is missing or escapes its root.");
        return path;
    }
}
