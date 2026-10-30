using FolderRewind.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RestorePreservePathsTests
{
    [TestMethod]
    public void CanonicalSelectorsMergeSubtreesWithoutMatchingSiblingDirectories()
    {
        CollectionAssert.AreEqual(new[] { "ftbquests/", "ftbteams/" },
            RestorePreservePaths.Normalize(["ftbquests/file.snbt", "ftbteams\\", "ftbquests/", "FTBTEAMS/"]));
        Assert.IsTrue(RestorePreservePaths.Matches("ftbteams/", "ftbteams/party/a.snbt"));
        Assert.IsFalse(RestorePreservePaths.Matches("ftbteams/", "ftbteams-old/a.snbt"));
        Assert.IsFalse(RestorePreservePaths.Matches("x.dat", "x.dat_old"));
    }

    [TestMethod]
    public void RejectsTraversalRootsGlobsAndAmbiguousEncoding()
    {
        foreach (var path in new[] { "", "/", "../x", "a/../x", "C:/x", "a//b", "./x", "a/*", "a?", "a,b", "a/./b", "x./y" })
            Assert.ThrowsExactly<System.IO.InvalidDataException>(() => RestorePreservePaths.Normalize([path]), path);
    }
}
