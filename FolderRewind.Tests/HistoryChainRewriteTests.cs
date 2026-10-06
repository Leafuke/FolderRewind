using FolderRewind.History.Domain;
using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteTests
{
    [TestMethod]
    public async Task MiddleDeletionRebasesAllBranchesAndReusesDescendantBytes()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "large.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        var branch = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "branch.txt"), "branch"));
        var plan = await f.Planner.PlanAsync(f.Delete(b));
        Assert.IsTrue(plan.CanExecute, string.Join(" ", plan.Blockers));
        Assert.HasCount(3, plan.Steps);
        await using var prepared = await f.Executor.PrepareAsync(plan);
        Assert.AreEqual(RepresentationKind.CoreSmartDelta, prepared.Mappings.Single(m => m.PreviousId == c.Representation.RepresentationId).Replacement.Kind);
        Assert.AreEqual(d.Entry.Locator.AbsolutePath, prepared.Mappings.Single(m => m.PreviousId == d.Representation.RepresentationId).Path);
        var result = await f.Executor.CommitAsync(prepared);
        Assert.IsTrue(result.Committed, result.Diagnostic);
        Assert.IsFalse(result.CleanupPending, result.Diagnostic);
        Assert.IsFalse(File.Exists(b.Entry.Locator.AbsolutePath));
        Assert.IsTrue(File.Exists(d.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(a, c, d, branch);
        await f.RestartAsync();
        await f.AssertRestoresAsync(c, d, branch);
    }

    [TestMethod]
    public async Task RootDeletionCreatesNewFullAndCancellationLeavesOldChain()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var plan = await f.Planner.PlanAsync(f.Delete(a));
        await using (var cancelled = await f.Executor.PrepareAsync(plan))
            Assert.AreEqual(RepresentationKind.CoreFull, cancelled.Mappings.Single().Replacement.Kind);
        await f.AssertRestoresAsync(a, b);
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(a)));
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        await f.AssertRestoresAsync(b);
    }

    [TestMethod]
    public async Task TerminalPayloadOnlyDeletionUsesDurableCatalogTransaction()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var request = f.Delete(a) with { HideTargets = false, ReleaseTargets = false };
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(request));
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        Assert.IsFalse(File.Exists(a.Entry.Locator.AbsolutePath));
        await f.RestartAsync();
        Assert.HasCount(0, (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }
}
