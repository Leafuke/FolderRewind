using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SevenZipExecutableLocatorTests
{
    [TestMethod]
    public void ConfiguredExecutableHasHighestPriority()
    {
        string configured = Path.GetFullPath("configured-7z.exe");
        string appCandidate = Path.GetFullPath(Path.Combine("app", "7z.exe"));

        string result = SevenZipExecutableLocator.FindFirstExisting(
            configured,
            Path.GetFullPath("app"),
            Path.GetFullPath("program-files"),
            Path.GetFullPath("program-files-x86"),
            Path.GetFullPath("path-dir"),
            path => string.Equals(path, configured, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, appCandidate, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(configured, result);
    }

    [TestMethod]
    public void RelativeConfiguredPathAlsoChecksApplicationDirectory()
    {
        string appBase = Path.GetFullPath("app");
        string expected = Path.GetFullPath(Path.Combine(appBase, "tools", "7za.exe"));

        string result = SevenZipExecutableLocator.FindFirstExisting(
            Path.Combine("tools", "7za.exe"),
            appBase,
            null,
            null,
            null,
            path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void ApplicationAndInstallDirectoriesPrecedePath()
    {
        string appBase = Path.GetFullPath("app");
        string programFiles = Path.GetFullPath("program-files");
        string pathDirectory = Path.GetFullPath("path-dir");
        string installedCandidate = Path.Combine(programFiles, "7-Zip", "7z.exe");
        string pathCandidate = Path.Combine(pathDirectory, "7z.exe");

        string result = SevenZipExecutableLocator.FindFirstExisting(
            null,
            appBase,
            programFiles,
            null,
            pathDirectory,
            path => string.Equals(path, installedCandidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, pathCandidate, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(installedCandidate, result);
    }

    [TestMethod]
    public void CandidateListIsCaseInsensitiveDistinct()
    {
        string appBase = Path.GetFullPath("app");
        var candidates = SevenZipExecutableLocator.BuildCandidates(
            Path.Combine(appBase, "7z.exe"),
            appBase,
            null,
            null,
            null);

        Assert.AreEqual(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
