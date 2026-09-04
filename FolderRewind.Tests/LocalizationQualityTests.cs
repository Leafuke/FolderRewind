using FolderRewind.Services;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalizationQualityTests
{
    [TestMethod]
    public void ResourceKeySetsAreSymmetricAndUnique()
    {
        var repositoryRoot = FindRepositoryRoot();
        var englishKeys = ReadResourceKeys(
            Path.Combine(repositoryRoot, "FolderRewind", "Strings", "en-US", "Resources.resw"));
        var chineseKeys = ReadResourceKeys(
            Path.Combine(repositoryRoot, "FolderRewind", "Strings", "zh-CN", "Resources.resw"));

        CollectionAssert.AreEquivalent(englishKeys, chineseKeys);
    }

    [TestMethod]
    public void UserDisplayFormatterUsesCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var value = new DateTime(2026, 9, 2, 17, 8, 9);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var englishDate = UserDisplayFormatter.Date(value);
            var englishDateTime = UserDisplayFormatter.LongDateTime(value);
            var englishNumber = UserDisplayFormatter.Number(12345.5, 1);
            var englishPersisted = UserDisplayFormatter.PersistedLocalDateTime(
                "2026/09/02 17:08",
                "yyyy/MM/dd HH:mm");

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            var chineseDate = UserDisplayFormatter.Date(value);
            var chineseDateTime = UserDisplayFormatter.LongDateTime(value);
            var chineseNumber = UserDisplayFormatter.Number(12345.5, 1);
            var chinesePersisted = UserDisplayFormatter.PersistedLocalDateTime(
                "2026/09/02 17:08",
                "yyyy/MM/dd HH:mm");

            Assert.AreEqual(value.ToString("d", CultureInfo.GetCultureInfo("en-US")), englishDate);
            Assert.AreEqual(value.ToString("G", CultureInfo.GetCultureInfo("en-US")), englishDateTime);
            Assert.AreEqual(12345.5.ToString("N1", CultureInfo.GetCultureInfo("en-US")), englishNumber);
            Assert.AreEqual(value.ToString("g", CultureInfo.GetCultureInfo("en-US")), englishPersisted);
            Assert.AreEqual(value.ToString("d", CultureInfo.GetCultureInfo("zh-CN")), chineseDate);
            Assert.AreEqual(value.ToString("G", CultureInfo.GetCultureInfo("zh-CN")), chineseDateTime);
            Assert.AreEqual(12345.5.ToString("N1", CultureInfo.GetCultureInfo("zh-CN")), chineseNumber);
            Assert.AreEqual(value.ToString("g", CultureInfo.GetCultureInfo("zh-CN")), chinesePersisted);
            Assert.AreNotEqual(englishDate, chineseDate);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    public void XamlDoesNotContainHardCodedChineseUiAttributes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repositoryRoot, "FolderRewind");
        var visibleTextPattern = new Regex(
            "(?:Content|Text|Header|PlaceholderText|Title|Description|AutomationProperties\\.(?:Name|HelpText))=\"[^\"]*\\p{IsCJKUnifiedIdeographs}",
            RegexOptions.CultureInvariant);

        var violations = Directory.EnumerateFiles(projectRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, lineNumber: index + 1)))
            .Where(item => visibleTextPattern.IsMatch(item.line))
            .Select(item => $"{Path.GetRelativePath(repositoryRoot, item.path)}:{item.lineNumber}: {item.line.Trim()}")
            .ToArray();

        Assert.HasCount(0, violations, string.Join(Environment.NewLine, violations));
    }

    private static string[] ReadResourceKeys(string path)
    {
        var keys = XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        Assert.AreEqual(keys.Length, keys.Distinct(StringComparer.Ordinal).Count(), $"Duplicate resource key in {path}");
        return keys;
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
