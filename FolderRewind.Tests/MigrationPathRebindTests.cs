using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MigrationPathRebindTests
{
    [TestMethod] public void RootMappingMatchesDirectoryBoundariesAndPreservesOtherPaths()
    {
        Assert.AreEqual(@"E:\Games\save", MigrationPathRebindPolicy.ReplaceRoot(@"D:\Games\save", @"d:\games\", @"E:\Games"));
        Assert.AreEqual(@"D:\GamesOther\save", MigrationPathRebindPolicy.ReplaceRoot(@"D:\GamesOther\save", @"D:\Games", @"E:\Games"));
        Assert.AreEqual(@"E:\Games", MigrationPathRebindPolicy.ReplaceRoot(@"D:\Games", @"D:\Games", @"E:\Games"));
        Assert.ThrowsExactly<ArgumentException>(() => MigrationPathRebindPolicy.ReplaceRoot(@"D:\Games", "relative", @"E:\Games"));
    }
}
