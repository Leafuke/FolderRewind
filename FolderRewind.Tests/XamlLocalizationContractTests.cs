using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;

namespace FolderRewind.Tests;

[TestClass]
public sealed class XamlLocalizationContractTests
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private sealed record ApiType(string Name, string? BaseType, HashSet<string> Writable, HashSet<string> AttachedSetters);

    [TestMethod]
    public void XamlUidPropertiesExistOnTheirActualControlTypes()
    {
        var root = RepositoryRoot();
        var project = Path.Combine(root, "FolderRewind");
        var api = ReadRestoredApis(project);
        var violations = new List<string>();
        var sites = Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .SelectMany(path => XDocument.Load(path, LoadOptions.SetLineInfo).Descendants()
                .Where(element => element.Attribute(Xaml + "Uid") is not null)
                .Select(element => (path, element))).ToArray();
        var checkedProperties = 0;
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(project, "Strings")))
        {
            var resources = XDocument.Load(Path.Combine(directory, "Resources.resw")).Root!.Elements("data")
                .Select(data => (string)data.Attribute("name")!).ToArray();
            foreach (var (path, element) in sites)
            {
                var name = ResolveType(element.Name, api);
                if (name is null) continue; // Application-defined controls are outside package metadata.
                var uid = (string)element.Attribute(Xaml + "Uid")!;
                foreach (var resource in resources.Where(key => key.StartsWith(uid + ".", StringComparison.Ordinal)))
                {
                    checkedProperties++;
                    var property = resource[(uid.Length + 1)..];
                    if (!CanAssign(name, property, api))
                        violations.Add($"{Path.GetRelativePath(root, path)}:{((IXmlLineInfo)element).LineNumber}: {element.Name.LocalName} x:Uid={uid}, {Path.GetFileName(directory)} resource {resource}");
                }
            }
        }
        Assert.IsGreaterThan(100, checkedProperties, "The restored WinUI/Toolkit metadata must cover the actual application XAML.");
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void MetadataRejectsTextOnButtonButAllowsContentAndInheritedProperties()
    {
        var api = ReadRestoredApis(Path.Combine(RepositoryRoot(), "FolderRewind"));
        const string button = "Microsoft.UI.Xaml.Controls.Button";
        Assert.IsFalse(CanAssign(button, "Text", api), "This mismatch caused settings navigation to throw XamlParseException.");
        Assert.IsTrue(CanAssign(button, "Content", api));
        Assert.IsTrue(CanAssign(button, "Width", api));
        Assert.IsTrue(CanAssign(button, "AutomationProperties.Name", api));
        Assert.IsTrue(CanAssign("Microsoft.UI.Xaml.Controls.TextBlock", "Text", api));
    }

    private static bool CanAssign(string type, string property, Dictionary<string, ApiType> api)
    {
        if (property.Contains('.'))
        {
            // Attached resources can use either AutomationProperties.Name or [using:namespace]Owner.Name.
            var match = Regex.Match(property, @"^(?:\[using:(?<ns>[^\]]+)\])?(?<owner>[^.]+)\.(?<property>[^.]+)$");
            if (!match.Success) return false;
            var owner = match.Groups["owner"].Value;
            var members = match.Groups["ns"].Success ? new[] { match.Groups["ns"].Value + "." + owner }
                : api.Keys.Where(key => key.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal) && key.EndsWith("." + owner, StringComparison.Ordinal));
            return members.Any(name => api.TryGetValue(name, out var definition) && definition.AttachedSetters.Contains("Set" + match.Groups["property"].Value));
        }
        var visited = new HashSet<string>();
        while (visited.Add(type) && api.TryGetValue(type, out var definition))
        {
            if (definition.Writable.Contains(property)) return true;
            if (definition.BaseType is null) break;
            type = definition.BaseType;
        }
        return false;
    }
    private static string? ResolveType(XName name, Dictionary<string, ApiType> api)
    {
        if (name.NamespaceName == Presentation)
            return api.Keys.FirstOrDefault(key => key.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal) && key.EndsWith("." + name.LocalName, StringComparison.Ordinal));
        if (name.NamespaceName.StartsWith("using:", StringComparison.Ordinal))
        {
            var type = name.NamespaceName[6..] + "." + name.LocalName;
            if (api.ContainsKey(type)) return type;
        }
        return null;
    }
    private static Dictionary<string, ApiType> ReadRestoredApis(string project)
    {
        using var assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(project, "obj", "project.assets.json")));
        var target = assets.RootElement.GetProperty("targets").EnumerateObject().First();
        var libraries = assets.RootElement.GetProperty("libraries");
        var folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name).ToArray();
        var result = new Dictionary<string, ApiType>(StringComparer.Ordinal);
        foreach (var library in target.Value.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("compile", out var files)
                || !libraries.TryGetProperty(library.Name, out var info) || !info.TryGetProperty("path", out var package)) continue;
            foreach (var file in files.EnumerateObject().Where(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var path = folders.Select(folder => Path.Combine(folder, package.GetString()!, file.Name.Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(File.Exists);
                if (path is null) continue;
                using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var reader = pe.GetMetadataReader();
                string? TypeName(EntityHandle handle) => handle.IsNil ? null : handle.Kind switch
                {
                    HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace) + "." + reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
                    HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Namespace) + "." + reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
                    _ => null
                };
                foreach (var handle in reader.TypeDefinitions)
                {
                    var definition = reader.GetTypeDefinition(handle); var name = TypeName(handle)!;
                    var writable = definition.GetProperties().Select(reader.GetPropertyDefinition)
                        .Where(property => !property.GetAccessors().Setter.IsNil)
                        .Select(property => reader.GetString(property.Name)).ToHashSet(StringComparer.Ordinal);
                    var setters = definition.GetMethods().Select(reader.GetMethodDefinition)
                        .Where(method => (method.Attributes & MethodAttributes.Static) != 0)
                        .Select(method => reader.GetString(method.Name)).Where(name => name.StartsWith("Set", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
                    result[name] = new(name, TypeName(definition.BaseType), writable, setters);
                }
            }
        }
        Assert.IsTrue(result.ContainsKey("Microsoft.UI.Xaml.Controls.Button"), "Restore the application project before checking XAML resource contracts.");
        return result;
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        { if (File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx"))) return directory.FullName; directory = directory.Parent; }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
