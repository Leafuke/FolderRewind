namespace FolderRewind.Tests;

[TestClass]
public sealed class DialogArchitectureTests
{
    [TestMethod]
    public void ContentDialogsAreShownOnlyByTheAppDialogService()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "FolderRewind");
        var violations = Directory
            .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("AppDialogService.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item => item.line.Contains(".ShowAsync()", StringComparison.Ordinal))
                .Select(item => $"{Path.GetRelativePath(projectRoot, path)}:{item.index + 1}"))
            .ToArray();

        Assert.HasCount(0, violations, $"ContentDialog.ShowAsync must be coordinated by IAppDialogService: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void LegacyDialogCoordinatorIsNotReferenced()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "FolderRewind");
        var references = Directory
            .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("TemplateDialogCoordinatorService", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(projectRoot, path))
            .ToArray();

        Assert.HasCount(0, references, $"Legacy dialog coordinator references: {string.Join(", ", references)}");
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
