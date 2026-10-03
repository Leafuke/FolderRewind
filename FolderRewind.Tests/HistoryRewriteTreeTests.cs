using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRewriteTreeTests
{
    [TestMethod]
    public async Task SameLengthAndTimestampCannotHideChangedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "rewrite-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "存档.txt");
            await File.WriteAllTextAsync(file, "aaaa");
            var time = File.GetLastWriteTimeUtc(file);
            var before = await HistoryRewriteTree.ReadAsync(root);
            await File.WriteAllTextAsync(file, "bbbb");
            File.SetLastWriteTimeUtc(file, time);
            var after = await HistoryRewriteTree.ReadAsync(root);
            Assert.IsFalse(before.EquivalentTo(after));
            CollectionAssert.AreEqual(new[] { "存档.txt" }, after.ChangedFrom(before).ToArray());
            Assert.IsFalse(after.RequiresFullAgainst(before));
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            Assert.IsTrue((await HistoryRewriteTree.ReadAsync(root)).RequiresFullAgainst(after));
        }
        finally { Directory.Delete(root, true); }
    }
}
