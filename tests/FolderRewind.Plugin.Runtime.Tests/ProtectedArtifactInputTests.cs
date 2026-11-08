using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ProtectedArtifactInputTests
{
    [TestMethod]
    public async Task ProtectedInventoryRetainsTheExactBytesUntilDisposed()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "protected-input-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(root, "input"); File.WriteAllText(path, "before");
        try
        {
            var content = new ArtifactContentHandle("input");
            await using var original = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [content] = root });
            var protectedRead = await original.ProtectAsync(default);
            var files = await protectedRead.ListFilesAsync(content, default);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(path, "change"));
            Assert.ThrowsExactly<IOException>(() => File.Move(path, path + ".old"));
            await using (var input = await protectedRead.OpenReadAsync(content, "input", default))
                Assert.AreEqual(files.Single().Sha256, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input)).ToLowerInvariant());
            await protectedRead.DisposeAsync();
            File.WriteAllText(path, "after");
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => protectedRead.OpenReadAsync(content, "input", default).AsTask());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProtectionFailureAndCancellationReleaseEarlierGuards()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "protected-input-" + Guid.NewGuid().ToString("N"))).FullName;
        var first = Path.Combine(root, "a"); var second = Path.Combine(root, "z"); File.WriteAllText(first, "a"); File.WriteAllText(second, "z");
        try
        {
            await using var read = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [new("input")] = root });
            using (File.Open(second, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                await Assert.ThrowsExactlyAsync<IOException>(() => read.ProtectAsync(default));
            File.WriteAllText(first, "released");
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => read.ProtectAsync(new(true)));
            File.Move(first, first + ".moved");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProtectedAllowedPathsDoNotExpandToExcludedLockedFiles()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "protected-input-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(root, "nested")); File.WriteAllText(Path.Combine(root, "nested", "data"), "data");
        try
        {
            using var gameLock = File.Open(Path.Combine(root, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var content = new ArtifactContentHandle("input");
            await using var read = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [content] = root }, new HashSet<string> { "nested/data" });
            await using var protectedRead = await read.ProtectAsync(default);
            Assert.AreEqual("nested/data", (await protectedRead.ListFilesAsync(content, default)).Single().RelativePath);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => protectedRead.OpenReadAsync(content, "session.lock", default).AsTask());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SpaceReservationsAggregateByVolumeAndReleaseForTheNextPhase()
    {
        var budget = new ArtifactSpaceBudget(path => new(path.StartsWith("other", StringComparison.Ordinal) ? "B" : "A", 1000));
        using (budget.Reserve(new ArtifactSpaceRequest("first", 600)))
        {
            using var otherVolume = budget.Reserve(new ArtifactSpaceRequest("other", 900));
            Assert.ThrowsExactly<IOException>(() => budget.Reserve(new ArtifactSpaceRequest("same-volume", 401)));
            using var fits = budget.Reserve(new ArtifactSpaceRequest("same-volume", 400));
        }
        using var nextPhase = budget.Reserve(new ArtifactSpaceRequest("first", 1000));
    }
}
