using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    public void PublicApiMatchesApprovedBaseline()
    {
        var actual = PublicApiSnapshot.Create(typeof(IFolderRewindPlugin).Assembly);
        var baseline = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PublicApi.txt"));
        var expectedFingerprint = baseline
            .SingleOrDefault(static line => line.StartsWith("# sha256:", StringComparison.Ordinal))?
            ["# sha256:".Length..]
            .Trim();
        var expectedTypes = baseline
            .Where(static line => line.StartsWith("T ", StringComparison.Ordinal))
            .ToArray();
        var actualFingerprint = PublicApiSnapshot.Fingerprint(actual);
        var actualTypes = actual
            .Where(static line => line.StartsWith("T ", StringComparison.Ordinal))
            .ToArray();

        Assert.IsNotNull(expectedFingerprint,
            $"Approve this API fingerprint and exported type list:\n# sha256:{actualFingerprint}\n{string.Join('\n', actualTypes)}");
        Assert.AreEqual(
            expectedFingerprint,
            actualFingerprint,
            $"The public member contract changed. Approve sha256:{actualFingerprint}");
        CollectionAssert.AreEqual(expectedTypes, actualTypes, "The exported type set changed.");
    }
}

internal static class PublicApiSnapshot
{
    public static string[] Create(Assembly assembly)
    {
        return assembly.GetExportedTypes()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .SelectMany(Describe)
            .ToArray();
    }

    public static string Fingerprint(IEnumerable<string> snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', snapshot));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IEnumerable<string> Describe(Type type)
    {
        yield return $"T {Format(type)}";
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
            yield return $"C {Format(type)}({string.Join(',', constructor.GetParameters().Select(parameter => Format(parameter.ParameterType)))})";
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            yield return $"P {Format(type)}.{property.Name}:{Format(property.PropertyType)}";
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(static value => !value.IsSpecialName && !IsCompilerGeneratedRecordMember(value))
                     .OrderBy(static value => value.Name, StringComparer.Ordinal)
                     .ThenBy(static value => value.ToString(), StringComparer.Ordinal))
        {
            yield return $"M {Format(type)}.{method.Name}({string.Join(',', method.GetParameters().Select(parameter => Format(parameter.ParameterType)))}):{Format(method.ReturnType)}";
        }
    }

    private static bool IsCompilerGeneratedRecordMember(MethodInfo method)
        => method.Name is "ToString" or "Equals" or "GetHashCode" or "Deconstruct" or "PrintMembers" or "<Clone>$";

    private static string Format(Type type)
    {
        if (type.IsArray) return Format(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var name = type.GetGenericTypeDefinition().FullName!;
        name = name[..name.IndexOf('`')];
        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(Format))}>";
    }
}
