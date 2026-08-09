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
    [DataRow("save:stream.7z", false)]
    [DataRow("backup.7z.", false)]
    public void SinglePathSegmentValidationRejectsTraversal(string value, bool expected)
    {
        Assert.AreEqual(expected, BackupStoragePathService.IsSafeSinglePathSegment(value));
    }

    [TestMethod]
    public void StorageNameSanitizationReplacesColonUsedByAlternateDataStreams()
    {
        var success = BackupStoragePathService.TryResolveStorageFolderName(
            "Monument Valley 2: Panoramic Edition SAVE",
            null,
            out var result);

        Assert.IsTrue(success);
        Assert.AreEqual("Monument Valley 2_ Panoramic Edition SAVE", result);
        Assert.IsTrue(BackupStoragePathService.IsSafeSinglePathSegment(result));
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
