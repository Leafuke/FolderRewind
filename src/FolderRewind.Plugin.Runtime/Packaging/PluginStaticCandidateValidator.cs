using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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
            root,
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
        string candidateRoot,
        string assemblyPath,
        string entryType,
        IReadOnlyList<string> declaredArchitectures)
    {
        var payloadPaths = Directory.EnumerateFiles(candidateRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var assemblies = new List<MetadataAssembly>();
        try
        {
            foreach (var path in payloadPaths)
            {
                var assembly = MetadataAssembly.Open(path);
                if (assembly is not null)
                {
                    assemblies.Add(assembly);
                }
            }

            var bySimpleName = new Dictionary<string, MetadataAssembly>(StringComparer.OrdinalIgnoreCase);
            var contractReferences = new List<(string Referrer, Version Version)>();
            foreach (var assembly in assemblies)
            {
                if (IsForbiddenHostAssembly(assembly.Name))
                {
                    throw new InvalidDataException(
                        $"Plugin payload assembly '{assembly.Path}' is a forbidden Host implementation assembly '{assembly.Name}'.");
                }
                if (!bySimpleName.TryAdd(assembly.Name, assembly))
                {
                    throw new InvalidDataException(
                        $"Plugin payload contains ambiguous managed assembly identity '{assembly.Name}'.");
                }

                foreach (var referenceHandle in assembly.Metadata.AssemblyReferences)
                {
                    var reference = assembly.Metadata.GetAssemblyReference(referenceHandle);
                    var referencedName = assembly.Metadata.GetString(reference.Name);
                    if (IsForbiddenHostAssembly(referencedName))
                    {
                        throw new InvalidDataException(
                            $"Plugin payload assembly '{assembly.Path}' references forbidden Host implementation assembly '{referencedName}'.");
                    }
                    if (string.Equals(
                        referencedName,
                        "FolderRewind.Plugin.Abstractions",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        contractReferences.Add((assembly.Path, reference.Version));
                    }
                }
            }

            if (contractReferences.Count == 0
                || contractReferences.Any(value => value.Version.Major != PluginApiVersion.HostVersion.Major))
            {
                throw new InvalidDataException(
                    "Plugin payload must reference FolderRewind.Plugin.Abstractions with the Host-compatible major version.");
            }

            var entry = assemblies.SingleOrDefault(value =>
                string.Equals(value.Path, Path.GetFullPath(assemblyPath), StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException("Plugin entryAssembly must be a managed .NET assembly.");
            }

            ValidateArchitecture(entry.Pe.PEHeaders.CoffHeader.Machine, declaredArchitectures);
            var entryHandle = FindEntryType(entry, entryType);
            var entryDefinition = entry.Metadata.GetTypeDefinition(entryHandle);
            var visibility = entryDefinition.Attributes & TypeAttributes.VisibilityMask;
            if (visibility != TypeAttributes.Public || entryDefinition.Attributes.HasFlag(TypeAttributes.Abstract))
            {
                throw new InvalidDataException("Plugin entry type must be public and concrete.");
            }

            if (!ReachesPluginContract(
                    new MetadataTypeNode(entry, entryHandle),
                    bySimpleName,
                    new HashSet<MetadataTypeVisitKey>()))
            {
                throw new InvalidDataException(
                    "Plugin entry type must reach IFolderRewindPlugin through its base types or interfaces.");
            }

            var hasConstructor = entryDefinition.GetMethods().Any(handle =>
            {
                var method = entry.Metadata.GetMethodDefinition(handle);
                return StringComparer.Ordinal.Equals(entry.Metadata.GetString(method.Name), ".ctor")
                    && method.Attributes.HasFlag(MethodAttributes.Public)
                    && method.GetParameters().Count == 0;
            });
            if (!hasConstructor)
            {
                throw new InvalidDataException("Plugin entry type requires a public parameterless constructor.");
            }
        }
        finally
        {
            foreach (var assembly in assemblies)
            {
                assembly.Dispose();
            }
        }
    }

    private static TypeDefinitionHandle FindEntryType(MetadataAssembly assembly, string entryType)
    {
        var separator = entryType.LastIndexOf('.');
        var expectedNamespace = separator < 0 ? string.Empty : entryType[..separator];
        var expectedName = separator < 0 ? entryType : entryType[(separator + 1)..];
        foreach (var handle in assembly.Metadata.TypeDefinitions)
        {
            var definition = assembly.Metadata.GetTypeDefinition(handle);
            if (StringComparer.Ordinal.Equals(assembly.Metadata.GetString(definition.Namespace), expectedNamespace)
                && StringComparer.Ordinal.Equals(assembly.Metadata.GetString(definition.Name), expectedName))
            {
                return handle;
            }
        }

        throw new InvalidDataException($"Plugin entry type '{entryType}' was not found in entryAssembly metadata.");
    }

    private static bool ReachesPluginContract(
        MetadataTypeNode node,
        IReadOnlyDictionary<string, MetadataAssembly> bySimpleName,
        ISet<MetadataTypeVisitKey> visited)
    {
        var key = new MetadataTypeVisitKey(node.Assembly.Identity, MetadataTokens.GetToken(node.Handle));
        if (!visited.Add(key))
        {
            return false;
        }

        if (IsPluginContract(node))
        {
            return true;
        }

        var definition = ResolveDefinition(node);
        if (definition is null)
        {
            return false;
        }

        foreach (var interfaceHandle in definition.Value.GetInterfaceImplementations())
        {
            var implementation = node.Assembly.Metadata.GetInterfaceImplementation(interfaceHandle);
            var interfaceNode = ResolveTypeNode(node.Assembly, implementation.Interface, bySimpleName);
            if (interfaceNode is not null && ReachesPluginContract(interfaceNode.Value, bySimpleName, visited))
            {
                return true;
            }
        }

        var baseNode = ResolveTypeNode(node.Assembly, definition.Value.BaseType, bySimpleName);
        return baseNode is not null && ReachesPluginContract(baseNode.Value, bySimpleName, visited);
    }

    private static bool IsPluginContract(MetadataTypeNode node)
    {
        if (node.Handle.Kind == HandleKind.TypeDefinition)
        {
            return string.Equals(node.Assembly.Name, "FolderRewind.Plugin.Abstractions", StringComparison.Ordinal)
                && string.Equals(
                    node.Assembly.Metadata.GetString(node.Assembly.Metadata.GetTypeDefinition((TypeDefinitionHandle)node.Handle).Namespace),
                    "FolderRewind.Plugin.Abstractions",
                    StringComparison.Ordinal)
                && string.Equals(
                    node.Assembly.Metadata.GetString(node.Assembly.Metadata.GetTypeDefinition((TypeDefinitionHandle)node.Handle).Name),
                    nameof(IFolderRewindPlugin),
                    StringComparison.Ordinal);
        }

        if (node.Handle.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var reference = node.Assembly.Metadata.GetTypeReference((TypeReferenceHandle)node.Handle);
        if (!string.Equals(node.Assembly.Metadata.GetString(reference.Namespace), "FolderRewind.Plugin.Abstractions", StringComparison.Ordinal)
            || !string.Equals(node.Assembly.Metadata.GetString(reference.Name), nameof(IFolderRewindPlugin), StringComparison.Ordinal)
            || reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var assemblyReference = node.Assembly.Metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
        return string.Equals(
            node.Assembly.Metadata.GetString(assemblyReference.Name),
            "FolderRewind.Plugin.Abstractions",
            StringComparison.Ordinal);
    }

    private static TypeDefinition? ResolveDefinition(MetadataTypeNode node)
    {
        if (node.Handle.Kind == HandleKind.TypeDefinition)
        {
            return node.Assembly.Metadata.GetTypeDefinition((TypeDefinitionHandle)node.Handle);
        }

        return null;
    }

    private static MetadataTypeNode? ResolveTypeNode(
        MetadataAssembly currentAssembly,
        EntityHandle handle,
        IReadOnlyDictionary<string, MetadataAssembly> bySimpleName)
    {
        if (handle.IsNil)
        {
            return null;
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            return new MetadataTypeNode(currentAssembly, handle);
        }
        if (handle.Kind != HandleKind.TypeReference)
        {
            throw new InvalidDataException(
                $"Plugin payload contains an unsupported type signature while resolving '{currentAssembly.Path}'.");
        }

        var referenceNode = new MetadataTypeNode(currentAssembly, handle);
        if (IsPluginContract(referenceNode))
        {
            return referenceNode;
        }

        var reference = currentAssembly.Metadata.GetTypeReference((TypeReferenceHandle)handle);
        return reference.ResolutionScope.Kind switch
        {
            HandleKind.ModuleDefinition => FindTypeDefinition(
                currentAssembly,
                currentAssembly.Metadata.GetString(reference.Namespace),
                currentAssembly.Metadata.GetString(reference.Name)),
            HandleKind.AssemblyReference => ResolveAssemblyTypeReference(currentAssembly, reference, bySimpleName),
            _ => throw new InvalidDataException(
                $"Plugin payload contains an unsupported nested type reference in '{currentAssembly.Path}'.")
        };
    }

    private static MetadataTypeNode? ResolveAssemblyTypeReference(
        MetadataAssembly currentAssembly,
        TypeReference reference,
        IReadOnlyDictionary<string, MetadataAssembly> bySimpleName)
    {
        var assemblyReference = currentAssembly.Metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
        var referencedName = currentAssembly.Metadata.GetString(assemblyReference.Name);
        if (string.Equals(referencedName, "FolderRewind.Plugin.Abstractions", StringComparison.Ordinal)
            || IsFrameworkAssembly(referencedName))
        {
            return null;
        }
        if (!bySimpleName.TryGetValue(referencedName, out var target))
        {
            throw new InvalidDataException(
                $"Plugin payload assembly '{currentAssembly.Path}' has unresolved non-framework dependency '{referencedName}'.");
        }

        return FindTypeDefinition(
            target,
            currentAssembly.Metadata.GetString(reference.Namespace),
            currentAssembly.Metadata.GetString(reference.Name));
    }

    private static MetadataTypeNode FindTypeDefinition(
        MetadataAssembly assembly,
        string expectedNamespace,
        string expectedName)
    {
        foreach (var handle in assembly.Metadata.TypeDefinitions)
        {
            var definition = assembly.Metadata.GetTypeDefinition(handle);
            if (string.Equals(assembly.Metadata.GetString(definition.Namespace), expectedNamespace, StringComparison.Ordinal)
                && string.Equals(assembly.Metadata.GetString(definition.Name), expectedName, StringComparison.Ordinal))
            {
                return new MetadataTypeNode(assembly, handle);
            }
        }

        throw new InvalidDataException(
            $"Plugin payload dependency '{assembly.Name}' does not define type '{expectedNamespace}.{expectedName}'.");
    }

    private static bool IsForbiddenHostAssembly(string name)
        => string.Equals(name, "FolderRewind", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "FolderRewind.Plugin.Runtime", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameworkAssembly(string name)
        => string.Equals(name, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "mscorlib", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);

    private readonly record struct MetadataTypeNode(MetadataAssembly Assembly, EntityHandle Handle);

    private readonly record struct MetadataTypeVisitKey(string AssemblyIdentity, int MetadataToken);

    private sealed class MetadataAssembly : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _pe;

        private MetadataAssembly(string path, FileStream stream, PEReader pe)
        {
            Path = path;
            _stream = stream;
            _pe = pe;
            Metadata = pe.GetMetadataReader();
            var definition = Metadata.GetAssemblyDefinition();
            Name = Metadata.GetString(definition.Name);
            Identity = $"{Name},{definition.Version}";
        }

        public string Path { get; }
        public string Name { get; }
        public string Identity { get; }
        public PEReader Pe => _pe;
        public MetadataReader Metadata { get; }

        public static MetadataAssembly? Open(string path)
        {
            FileStream? stream = null;
            PEReader? pe = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
                {
                    pe.Dispose();
                    stream.Dispose();
                    return null;
                }

                return new MetadataAssembly(path, stream, pe);
            }
            catch
            {
                pe?.Dispose();
                stream?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _pe.Dispose();
            _stream.Dispose();
        }
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
