using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ControlledArtifactInputTests
{
    [TestMethod]
    public async Task ChangedViewDoesNotReadOrExposeUnchangedFilesAndReusesItsInventory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "changed"), [1, 2]);
            await File.WriteAllBytesAsync(Path.Combine(root, "unchanged"), [3]);
            using var locked = new FileStream(Path.Combine(root, "unchanged"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var handle = new ArtifactContentHandle("primary");
            var read = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [handle] = root },
                new HashSet<string>(StringComparer.Ordinal) { "changed" });
            var files = await read.ListFilesAsync(handle, default);
            Assert.HasCount(1, files); Assert.AreEqual("changed", files[0].RelativePath);
            Assert.AreSame(files, await read.ListFilesAsync(handle, default));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () => await read.OpenReadAsync(handle, "unchanged", default));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await read.OpenReadAsync(handle, "../outside", default));
            await using var input = await read.OpenReadAsync(handle, "changed", default);
            Assert.AreEqual(1, input.ReadByte());
        }
        finally { Directory.Delete(root, true); }
    }
}
