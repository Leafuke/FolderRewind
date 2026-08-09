using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public class BackupPathOverlapPolicyTests
{
    [TestMethod]
    public void Validate_RejectsSameAndBothParentChildDirections()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind-overlap", "source");

        Assert.AreEqual(
            BackupPathOverlapKind.SamePath,
            BackupPathOverlapPolicy.Validate(root, root + Path.DirectorySeparatorChar).Kind);
        Assert.AreEqual(
            BackupPathOverlapKind.TargetInsideSource,
            BackupPathOverlapPolicy.Validate(root, Path.Combine(root, "archives")).Kind);
        Assert.AreEqual(
            BackupPathOverlapKind.SourceInsideTarget,
            BackupPathOverlapPolicy.Validate(Path.Combine(root, "child"), root).Kind);
    }

    [TestMethod]
    public void Validate_AllowsSiblingWithSharedPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind-overlap");
        var result = BackupPathOverlapPolicy.Validate(
            Path.Combine(root, "game"),
            Path.Combine(root, "game-backups"),
            Path.Combine(root, "metadata"));

        Assert.IsTrue(result.IsSafe);
    }

    [TestMethod]
    public void Validate_NormalizesRelativeSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind-overlap", "source");
        var target = Path.Combine(root, "child", "..", "archives");

        Assert.AreEqual(
            BackupPathOverlapKind.TargetInsideSource,
            BackupPathOverlapPolicy.Validate(root, target).Kind);
    }

    [TestMethod]
    public void Validate_ResolvesDirectoryLinksWhenSupported()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "FolderRewind-overlap-links", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var link = Path.Combine(testRoot, "source-link");
        Directory.CreateDirectory(source);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, source);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Directory links are unavailable in this environment: {ex.Message}");
                return;
            }

            Assert.AreEqual(
                BackupPathOverlapKind.TargetInsideSource,
                BackupPathOverlapPolicy.Validate(source, Path.Combine(link, "archives")).Kind);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
