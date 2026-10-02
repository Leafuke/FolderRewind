using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RestoreWhitelistPreparationTests
{
    [TestMethod]
    public async Task CleanRetainsOnlyMissingWhitelistedPathsAndDoesNotMaskArchiveFiles()
    {
        var current = new View(new Dictionary<string, byte[]>
        {
            ["keep/old.txt"] = Encoding.UTF8.GetBytes("current"),
            ["keep/shared.txt"] = Encoding.UTF8.GetBytes("current shared"),
            ["discard.txt"] = [1]
        });
        var target = new View(new Dictionary<string, byte[]> { ["keep/shared.txt"] = Encoding.UTF8.GetBytes("backup shared") });
        var files = await RestoreWhitelistPreparation.PrepareAsync(current, target, ["keep"], "D:/test-world", CancellationToken.None);
        Assert.HasCount(1, files);
        Assert.AreEqual("keep/old.txt", files[0].RelativePath);
        Assert.AreEqual("current", Encoding.UTF8.GetString(files[0].Content.Span));
        Assert.AreEqual("backup shared", Encoding.UTF8.GetString(target.Files["keep/shared.txt"]));
    }

    [TestMethod]
    public async Task EmptyRulesDoNotPreserveAnything()
    {
        var files = await RestoreWhitelistPreparation.PrepareAsync(new View(new() { ["file"] = [1] }),
            new View(new()), [], "D:/test-world", CancellationToken.None);
        Assert.IsEmpty(files);
    }

    [TestMethod]
    public async Task CancellationAndLimitsFailInsteadOfReturningPartialPreservation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RestoreWhitelistPreparation.PrepareAsync(
            new View(new() { ["keep/file"] = [1] }), new View(new()), ["keep"], "D:/test-world", cancellation.Token));
        var entries = Enumerable.Range(0, 4097).ToDictionary(index => $"keep/{index}.txt", _ => new byte[] { 1 });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RestoreWhitelistPreparation.PrepareAsync(
            new View(entries), new View(new()), ["keep"], "D:/test-world", CancellationToken.None));
    }

    private sealed class View(Dictionary<string, byte[]> files) : IRestoreSourceView
    {
        public Dictionary<string, byte[]> Files => files;
        public IReadOnlyList<string> RelativePaths => files.Keys.Order(StringComparer.Ordinal).ToArray();
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
            => ValueTask.FromResult<Stream>(new MemoryStream(files[relativePath], writable: false));
    }
}
