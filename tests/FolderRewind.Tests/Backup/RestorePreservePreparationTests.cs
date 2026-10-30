using FolderRewind.Services;
using FolderRewind.Plugin.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RestorePreservePreparationTests
{
    private sealed class View(Dictionary<string, string> files) : IRestoreSourceView
    {
        public IReadOnlyList<string> RelativePaths => files.Keys.ToArray();
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
            => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(files[path])));
    }

    [TestMethod]
    public async Task CopiesCurrentAndDeletesHistoricalAndPluginOnlyFiles()
    {
        var current = new View(new() { ["world/level.dat"] = "world", ["world/ftbquests/a.snbt"] = "new", ["world/ftbteams/new.snbt"] = "new team" });
        var target = new View(new() { ["world/level.dat"] = "old", ["world/ftbquests/a.snbt"] = "old", ["world/ftbteams/deleted.snbt"] = "old team" });
        var plan = await RestorePreservePreparation.PrepareAsync(current, target, ["ftbquests/", "ftbteams/"], true, _ => true, default, ["world/ftbteams/plugin.snbt"]);
        Assert.HasCount(2, plan.Files);
        Assert.AreEqual("new", Encoding.UTF8.GetString(plan.Files.Single(file => file.RelativePath.EndsWith("a.snbt")).Content.Span));
        CollectionAssert.AreEquivalent(new[] { "world/ftbteams/deleted.snbt", "world/ftbteams/plugin.snbt" }, plan.Deletes.ToArray());
    }

    [TestMethod]
    public async Task MissingCurrentSubtreeDeletesHistoricalSubtree()
    {
        var plan = await RestorePreservePreparation.PrepareAsync(new View(new()), new View(new() { ["ftbteams/old.snbt"] = "old" }), ["ftbteams/"], false, _ => true, default);
        Assert.IsEmpty(plan.Files);
        CollectionAssert.AreEqual(new[] { "ftbteams/old.snbt" }, plan.Deletes.ToArray());
    }

    [TestMethod]
    public async Task AmbiguousWorldOrExcludedSelectedFileFailsBeforeOutput()
    {
        var current = new View(new() { ["world/level.dat"] = "world", ["other/level.dat"] = "other" });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RestorePreservePreparation.PrepareAsync(current, new View(new()), ["ftbteams/"], true, _ => true, default));
        current = new View(new() { ["ftbteams/private.snbt"] = "private" });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RestorePreservePreparation.PrepareAsync(current, new View(new()), ["ftbteams/"], false, path => !path.EndsWith("private.snbt"), default));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RestorePreservePreparation.PrepareAsync(current, new View(new()), ["ftbteams/"], false, _ => true, new CancellationToken(true)));
    }
}
