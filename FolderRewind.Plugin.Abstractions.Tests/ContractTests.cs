using System.Reflection;
using System.Globalization;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Abstractions.Tests;

[TestClass]
public sealed class ContractTests
{
    [TestMethod]
    public void AssemblyIdentityIsStableForApiThree()
    {
        var assembly = typeof(IFolderRewindPlugin).Assembly.GetName();

        Assert.AreEqual("FolderRewind.Plugin.Abstractions", assembly.Name);
        Assert.AreEqual(new Version(3, 0, 0, 0), assembly.Version);
    }

    [TestMethod]
    public void HostAcceptsSameMajorAndAtLeastRequestedMinor()
    {
        Assert.IsTrue(new PluginApiVersion(3, 0).IsSatisfiedBy(new PluginApiVersion(3, 0)));
        Assert.IsTrue(new PluginApiVersion(3, 0).IsSatisfiedBy(new PluginApiVersion(3, 2)));
        Assert.IsFalse(new PluginApiVersion(3, 2).IsSatisfiedBy(new PluginApiVersion(3, 1)));
        Assert.IsFalse(new PluginApiVersion(2, 9).IsSatisfiedBy(new PluginApiVersion(3, 9)));
        Assert.AreEqual(new PluginApiVersion(3, 6), PluginApiVersion.HostVersion);
    }

    [TestMethod]
    public void DiscoveryDefinitionCatalogIsAnOptionalMetadataExtension()
    {
        Assert.IsFalse(typeof(IPluginCapability).IsAssignableFrom(typeof(IDiscoveryDefinitionCatalog)));
        var definition = new DiscoveryDefinitionDescriptor(
            "minecraft-java",
            "Minecraft: Java Edition",
            ["Minecraft"],
            new Dictionary<string, string>());

        Assert.AreEqual("minecraft-java", definition.DefinitionId);
    }

    [TestMethod]
    public void FolderMetadataKeepsStableKeysSeparateFromLocalizedPresentation()
    {
        var displayName = new LocalizedText(
            "World name",
            new Dictionary<string, string> { ["zh-CN"] = "世界名称" });
        var value = new LocalizedText("Survival", new Dictionary<string, string> { ["zh-CN"] = "生存模式" });
        var field = new FolderMetadataField("gameMode", displayName, value);
        var result = new FolderMetadataResult([field], Array.Empty<PluginDiagnostic>());

        Assert.AreEqual("gameMode", result.Fields[0].Key);
        Assert.AreEqual("World name", result.Fields[0].DisplayName.Default);
        Assert.AreEqual("生存模式", result.Fields[0].Value.Translations["zh-CN"]);
    }

    [TestMethod]
    public void CaptureTimeMetadataUsesHostOwnedSourceViewInsteadOfLiveFolderRequest()
    {
        Assert.AreNotEqual(typeof(FolderMetadataRequest), typeof(VersionMetadataCaptureRequest));
        Assert.AreEqual(
            typeof(IVersionMetadataSourceView),
            typeof(VersionMetadataCaptureRequest).GetProperty(nameof(VersionMetadataCaptureRequest.Source))!.PropertyType);
    }

    [TestMethod]
    public void LegacyConsistencyLeaseIsNotAssumedToExposeStableCaptureView()
    {
        IConsistencyLease lease = new LegacyConsistencyLease();

        Assert.IsFalse(lease.IsStableSourceView);
    }

    [TestMethod]
    public void RoleSpecificIdentitiesRemainDifferentClrTypes()
    {
        var text = "com.folderrewind.minerewind";
        var plugin = new PluginId(text);
        var discovery = new DiscoveryProviderId(text);
        var state = new StateOwnerId(text);

        Assert.AreEqual(text, plugin.Value);
        Assert.AreEqual(text, discovery.Value);
        Assert.AreEqual(text, state.Value);
        Assert.AreNotEqual(typeof(PluginId), typeof(DiscoveryProviderId));
        Assert.AreNotEqual(typeof(PluginId), typeof(StateOwnerId));
    }

    [TestMethod]
    [DataRow("MineRewind")]
    [DataRow("com.FolderRewind.plugin")]
    [DataRow("single")]
    [DataRow("com..plugin")]
    public void PluginIdRejectsNonCanonicalValues(string value)
        => Assert.ThrowsExactly<ArgumentException>(() => new PluginId(value));

    [TestMethod]
    public void AbstractionsReferenceOnlyBclAssemblies()
    {
        var references = typeof(IFolderRewindPlugin).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(references.Any(name =>
            name.StartsWith("Microsoft.UI", StringComparison.Ordinal)
            || name.StartsWith("FolderRewind", StringComparison.Ordinal)
            || name.StartsWith("CommunityToolkit", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BackupRequestOptionsRemainCompatibleWithLegacyServiceImplementations()
    {
        var legacy = new LegacyBackupRequestService();
        IBackupRequestService service = legacy;

        var outcome = await service.RequestAsync(
            "config",
            null,
            new BackupRequestOptions { Comment = "checkpoint" },
            CancellationToken.None);

        Assert.AreEqual(OperationOutcome.Success, outcome);
        Assert.AreEqual(1, legacy.RequestCount);
    }

    [TestMethod]
    public void PublicApiMatchesApprovedBaseline()
    {
        var actual = PublicApiSnapshot.Create(typeof(IFolderRewindPlugin).Assembly);
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PublicApi.txt"))
            .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .ToArray();
        var removed = expected.Except(actual, StringComparer.Ordinal);
        var added = actual.Except(expected, StringComparer.Ordinal);
        Assert.IsTrue(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"The public API contract changed. Review PublicApi.txt; it is never updated automatically.\n"
            + $"Removed signatures:\n{string.Join('\n', removed)}\n"
            + $"Added signatures:\n{string.Join('\n', added)}\n"
            + "If both lists are empty, restore the ordinal ordering of the baseline.");
    }

    [TestMethod]
    public void CoreCaptureModePreservesReleasedNumericContract()
    {
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2 },
            Enum.GetValues<CoreCaptureMode>().Select(static value => (int)value).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "Full", "Smart", "Rolling" },
            Enum.GetNames<CoreCaptureMode>());
    }

    private sealed class LegacyBackupRequestService : IBackupRequestService
    {
        public int RequestCount { get; private set; }

        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(OperationOutcome.Success);
        }
    }

    private sealed class LegacyConsistencyLease : IConsistencyLease
    {
        public string SourcePath => "live";
        public IReadOnlyList<PluginDiagnostic> Diagnostics => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class PublicApiSnapshot
{
    public static string[] Create(Assembly assembly)
    {
        return assembly.GetExportedTypes()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .SelectMany(Describe)
            .OrderBy(static signature => signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> Describe(Type type)
    {
        var kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";
        var modifiers = type.IsAbstract && type.IsSealed ? "static "
            : type.IsInterface || type.IsValueType ? ""
            : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "";
        var bases = new List<string>();
        if (type.IsEnum) bases.Add(Format(Enum.GetUnderlyingType(type)));
        else if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
            bases.Add(Format(baseType));
        bases.AddRange(type.GetInterfaces().Select(Format).Order(StringComparer.Ordinal));
        yield return $"T public {modifiers}{kind} {Format(type)}"
            + (bases.Count == 0 ? "" : $" : {string.Join(',', bases)}");
        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type))
            {
                yield return $"E {Format(type)}.{name}={Convert.ToInt64(Enum.Parse(type, name))}";
            }
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(static value => value.ToString(), StringComparer.Ordinal))
        {
            yield return $"C {Modifiers(constructor)}{Format(type)}({Parameters(constructor)})";
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            var accessors = new List<string>();
            if (property.GetMethod is { } getter) accessors.Add($"{Modifiers(getter)}get;");
            if (property.SetMethod is { } setter)
            {
                var isInit = setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));
                accessors.Add($"{Modifiers(setter)}{(isInit ? "init" : "set")};");
            }
            var index = property.GetIndexParameters();
            var indexSignature = index.Length == 0 ? "" : $"[{string.Join(',', index.Select(Parameter))}]";
            yield return $"P {Format(type)}.{property.Name}{indexSignature}:{Format(property.PropertyType)}"
                + $" {{ {string.Join(' ', accessors)} }}";
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(static value => !value.IsSpecialName && !IsCompilerGeneratedRecordMember(value))
                     .OrderBy(static value => value.Name, StringComparer.Ordinal)
                     .ThenBy(static value => value.ToString(), StringComparer.Ordinal))
        {
            var generic = method.IsGenericMethod ? $"<{string.Join(',', method.GetGenericArguments().Select(Format))}>" : "";
            yield return $"M {Modifiers(method)}{Format(type)}.{method.Name}{generic}({Parameters(method)}):{Format(method.ReturnType)}";
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(static field => !field.IsSpecialName && !field.DeclaringType!.IsEnum))
            yield return $"F public {(field.IsLiteral ? "const " : field.IsStatic ? "static " : "")}"
                + $"{(field.IsInitOnly ? "readonly " : "")}{Format(type)}.{field.Name}:{Format(field.FieldType)}"
                + (field.IsLiteral ? $"={Constant(field.GetRawConstantValue())}" : "");

        foreach (var entry in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            yield return $"V {Modifiers(entry.AddMethod!)}{Format(type)}.{entry.Name}:{Format(entry.EventHandlerType!)}";
    }

    private static string Modifiers(MethodBase method)
    {
        var visibility = method.IsPublic ? "public " : method.IsFamilyOrAssembly ? "protected internal "
            : method.IsFamily ? "protected " : method.IsAssembly ? "internal " : "private ";
        return visibility + (method.IsStatic ? "static " : "")
            + (method.IsAbstract ? "abstract " : method.IsVirtual && !method.IsFinal ? "virtual " : "");
    }

    private static string Parameters(MethodBase method) => string.Join(',', method.GetParameters().Select(Parameter));

    private static string Parameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var modifier = type.IsByRef ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref "
            : parameter.IsDefined(typeof(ParamArrayAttribute)) ? "params " : "";
        return $"{modifier}{Format(type.IsByRef ? type.GetElementType()! : type)} {parameter.Name}"
            + (parameter.HasDefaultValue ? $"={Constant(parameter.DefaultValue)}" : parameter.IsOptional ? "=<optional>" : "");
    }

    private static string Constant(object? value) => value is null ? "null"
        : value is Missing or DBNull ? "<missing>"
        : value is Enum enumeration ? Convert.ToString(enumeration.ToString("D"), CultureInfo.InvariantCulture)!
        : JsonSerializer.Serialize(value, value.GetType());

    private static bool IsCompilerGeneratedRecordMember(MethodInfo method)
        => method.Name is "ToString" or "Equals" or "GetHashCode" or "Deconstruct" or "PrintMembers" or "<Clone>$";

    private static string Format(Type type)
    {
        if (type.IsArray) return Format(type.GetElementType()!) + "[]";
        if (type.IsByRef) return Format(type.GetElementType()!) + "&";
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var name = type.GetGenericTypeDefinition().FullName!;
        name = name[..name.IndexOf('`')];
        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(Format))}>";
    }
}
