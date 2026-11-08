using FolderRewind.History.Merge;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MergeSourceBoundaryTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fr-merge-scope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public async Task ExcludedLinkedFileAndSubtreeDoNotBlockOrEnterTheManifest()
    {
        var source = Path.Combine(_root, "source"); var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(source); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        var secret = Path.Combine(outside, "secret"); File.WriteAllText(secret, "unrelated");
        Directory.CreateSymbolicLink(Path.Combine(source, "cache"), outside);
        File.CreateSymbolicLink(Path.Combine(source, "ignored-link"), secret);
        using var locked = new FileStream(secret, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var manifest = await MergeTreeManifest.ReadAsync(source, p => p == "keep.txt", default,
            includeDirectory: _ => false, mayDescend: _ => false);
        CollectionAssert.AreEqual(new[] { "keep.txt" }, manifest.Files.Keys.ToArray());
        Assert.IsEmpty(manifest.Directories);
        await Assert.ThrowsExactlyAsync<IOException>(() => MergeTreeManifest.ReadAsync(source, _ => true, default));
    }

    [TestMethod]
    public async Task WhitelistAncestorsRemainTraversableAndSelectedLinksStillFail()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ancestor", "selected"));
        File.WriteAllText(Path.Combine(_root, "ancestor", "selected", "keep.txt"), "keep");
        bool FileIncluded(string p) => p == "ancestor/selected/keep.txt";
        bool Descend(string p) => p is "ancestor" or "ancestor/selected";
        var manifest = await MergeTreeManifest.ReadAsync(_root, FileIncluded, default, _ => false, Descend);
        Assert.HasCount(1, manifest.Files);
        CollectionAssert.AreEqual(new[] { "ancestor", "ancestor/selected" }, manifest.Directories.ToArray());
        File.CreateSymbolicLink(Path.Combine(_root, "selected-link"), Path.Combine(_root, "ancestor", "selected", "keep.txt"));
        await Assert.ThrowsExactlyAsync<IOException>(() => MergeTreeManifest.ReadAsync(_root,
            p => FileIncluded(p) || p == "selected-link", default, _ => false, Descend));
    }
}
