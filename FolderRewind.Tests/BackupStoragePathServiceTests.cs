using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupStoragePathServiceTests
{
    [TestMethod]
    [DataRow("World", true)]
    [DataRow("World.7z", true)]
    [DataRow("", false)]
    [DataRow(".", false)]
    [DataRow("..", false)]
    [DataRow("parent/child", false)]
    [DataRow("parent\\child", false)]
    public void SinglePathSegmentValidationRejectsTraversal(string value, bool expected)
    {
        Assert.AreEqual(expected, BackupStoragePathService.IsSafeSinglePathSegment(value));
    }

    [TestMethod]
    public void BuildPathRejectsParentTraversalAndPrefixSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind-PathRoot");

        Assert.IsFalse(BackupStoragePathService.TryBuildPathWithinRoot(root, "..\\outside", out _));
        Assert.IsFalse(BackupStoragePathService.IsPathInsideRoot(root + "-other", root));
        Assert.IsTrue(BackupStoragePathService.TryBuildPathWithinRoot(root, "World", out var result));
        Assert.IsTrue(BackupStoragePathService.IsPathInsideRoot(result, root));
    }
}
