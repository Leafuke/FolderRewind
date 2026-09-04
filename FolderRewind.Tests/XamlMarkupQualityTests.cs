using System.Xml.Linq;

namespace FolderRewind.Tests;

[TestClass]
public sealed class XamlMarkupQualityTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void DataTemplatesDeclareCompileTimeTypes()
    {
        var templatesWithoutTypes = LoadXamlDocuments()
            .SelectMany(item => item.document
                .Descendants()
                .Where(element => element.Name.LocalName == "DataTemplate")
                .Where(element => element.Attribute(XamlNamespace + "DataType") is null)
                .Select(_ => item.path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.HasCount(0, templatesWithoutTypes, $"DataTemplates without x:DataType: {string.Join(", ", templatesWithoutTypes)}");
    }

    [TestMethod]
    public void StableStylesUseStaticResourceLookup()
    {
        var dynamicStyleLookups = LoadXamlDocuments()
            .SelectMany(item => item.document
                .Descendants()
                .Attributes("Style")
                .Where(attribute => attribute.Value.StartsWith("{ThemeResource ", StringComparison.Ordinal))
                .Select(attribute => $"{item.path}:{attribute.Value}"))
            .ToArray();

        Assert.HasCount(0, dynamicStyleLookups, $"Stable styles should use StaticResource: {string.Join(", ", dynamicStyleLookups)}");
    }

    private static IEnumerable<(string path, XDocument document)> LoadXamlDocuments()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "FolderRewind");
        return Directory
            .EnumerateFiles(projectRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path.GetRelativePath(projectRoot, path), XDocument.Load(path)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the FolderRewind repository root.");
    }
}
