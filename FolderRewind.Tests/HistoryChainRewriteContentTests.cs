using FolderRewind.History.Domain;
using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteContentTests
{
    [TestMethod]
    [DataRow("modify-modify")]
    [DataRow("modify-delete")]
    [DataRow("add-delete")]
    [DataRow("delete-create")]
    [DataRow("only-delete")]
    [DataRow("timestamp")]
    [DataRow("file-directory")]
    [DataRow("empty-unicode")]
    public async Task ComposedDeltaPreservesFinalState(string scenario)
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p =>
        {
            File.WriteAllBytes(Path.Combine(p, "large.bin"), new byte[20000]);
            File.WriteAllText(Path.Combine(p, "x.txt"), "base");
            File.WriteAllText(Path.Combine(p, "y.txt"), "base-y");
        });
        var b = await f.AddAsync(a, p =>
        {
            switch (scenario)
            {
                case "add-delete": File.WriteAllText(Path.Combine(p, "new.txt"), "new"); break;
                case "delete-create": case "only-delete": case "file-directory": File.Delete(Path.Combine(p, "x.txt")); break;
                case "timestamp": File.SetLastWriteTimeUtc(Path.Combine(p, "x.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); break;
                default: File.WriteAllText(Path.Combine(p, "x.txt"), "changed-B"); break;
            }
        });
        var c = await f.AddAsync(b, p =>
        {
            switch (scenario)
            {
                case "modify-modify": case "delete-create": File.WriteAllText(Path.Combine(p, "x.txt"), "changed-C"); break;
                case "modify-delete": File.Delete(Path.Combine(p, "x.txt")); break;
                case "add-delete": File.Delete(Path.Combine(p, "new.txt")); break;
                case "only-delete": File.Delete(Path.Combine(p, "y.txt")); break;
                case "file-directory": Directory.CreateDirectory(Path.Combine(p, "x.txt")); File.WriteAllText(Path.Combine(p, "x.txt", "child"), "child"); break;
                case "empty-unicode": File.WriteAllText(Path.Combine(p, "空文件.txt"), string.Empty); break;
            }
        });
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b)));
        var mapping = prepared.Mappings.Single();
        Assert.AreEqual(scenario == "file-directory" ? RepresentationKind.CoreFull : RepresentationKind.CoreSmartDelta, mapping.Replacement.Kind);
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        await f.AssertRestoresAsync(a, c);
    }

    [TestMethod]
    public async Task EmptyTargetBecomesValidFullArchive()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "last.txt"), "last"));
        var b = await f.AddAsync(a, p => File.Delete(Path.Combine(p, "last.txt")));
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(a)));
        Assert.AreEqual(RepresentationKind.CoreFull, prepared.Mappings.Single().Replacement.Kind);
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        await f.AssertRestoresAsync(b);
    }
}
