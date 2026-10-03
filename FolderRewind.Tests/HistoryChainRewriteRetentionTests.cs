using FolderRewind.History.Domain;
using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteRetentionTests
{
    [TestMethod]
    public async Task RetentionRebuildsOldestKeptBoundaryOnceAndPreservesFollowingDelta()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var bytes = new byte[20000]; new Random(1).NextBytes(bytes);
        var a = await f.AddAsync(null, p => { File.WriteAllBytes(Path.Combine(p, "old.bin"), bytes); File.WriteAllText(Path.Combine(p, "keep.txt"), "keep"); });
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.Delete(Path.Combine(p, "old.bin")));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        foreach (var node in f.Nodes) await f.RecordBackupAsync(node);
        var unlimited = await f.Retention().ExecuteAsync(0, 5);
        Assert.IsFalse(unlimited.Committed);
        var underLimit = await f.Retention().ExecuteAsync(10, 5);
        Assert.IsFalse(underLimit.Committed);
        var result = await f.Retention().ExecuteAsync(2, 5);
        Assert.IsTrue(result.Committed, result.Diagnostic);
        Assert.IsGreaterThan(0L, result.NetReleasedBytes);
        Assert.IsFalse(File.Exists(a.Entry.Locator.AbsolutePath));
        Assert.IsFalse(File.Exists(b.Entry.Locator.AbsolutePath));
        Assert.IsTrue(File.Exists(d.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(c, d);
        var again = await f.Retention().ExecuteAsync(2, 5);
        Assert.IsFalse(again.Committed, again.Diagnostic);
        Assert.IsFalse((await f.History.Query.GetAnnotationProjectionAsync(new(HistoryAnnotationTargetKind.Version, a.Version.VersionId.Value))).IsSuppressed);
    }

    [TestMethod]
    public async Task PinnedVersionAndItsAlternateRepresentationSurviveRetention()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "keep.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        foreach (var node in f.Nodes) await f.RecordBackupAsync(node);
        await f.History.Annotations.SetPinAsync(new(HistoryAnnotationTargetKind.Version, a.Version.VersionId.Value), true);
        var result = await f.Retention().ExecuteAsync(1, 5);
        Assert.IsTrue(result.Committed, result.Diagnostic);
        Assert.IsGreaterThan(0L, result.NetReleasedBytes);
        await f.AssertRestoresAsync(a, c);
        var manual = await f.Planner.PlanAsync(f.Delete(a));
        Assert.IsFalse(manual.CanExecute);
    }
}
