using FolderRewind.History.Domain;
using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteRecoveryTests
{
    [TestMethod]
    [DataRow("payload-created")]
    [DataRow("replacement-verified")]
    [DataRow("before-pack")]
    public async Task PreCommitFailuresKeepArchivesAndVisibility(string phase)
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "base.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            step => { if (step == phase) throw new InvalidOperationException("injected crash"); });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var prepared = await executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b)));
            await executor.CommitAsync(prepared);
        });
        Assert.AreEqual(catalog.CatalogRevision, (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision);
        Assert.IsTrue(File.Exists(b.Entry.Locator.AbsolutePath));
        Assert.IsFalse((await f.History.Query.GetAnnotationProjectionAsync(new(HistoryAnnotationTargetKind.Version, b.Version.VersionId.Value))).IsSuppressed);
        await f.RestartAsync();
        await f.AssertRestoresAsync(a, b, c);
    }

    [TestMethod]
    [DataRow("pack-installed")]
    [DataRow("catalog-applied")]
    [DataRow("cache-repaired")]
    [DataRow("before-reclamation")]
    [DataRow("file-reclaimed")]
    public async Task CommittedFailuresRecoverIdempotently(string phase)
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "base.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            step => { if (step == phase) throw new InvalidOperationException("injected crash"); });
        await using (var prepared = await executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b))))
        {
            var result = await executor.CommitAsync(prepared);
            Assert.IsTrue(result.Committed, result.Diagnostic);
            Assert.IsTrue(result.CleanupPending);
        }
        await f.RestartAsync();
        await f.AssertRestoresAsync(a, c);
        Assert.IsFalse(File.Exists(b.Entry.Locator.AbsolutePath));
        await f.RestartAsync();
        await f.AssertRestoresAsync(c);
        Assert.IsTrue((await f.History.Query.GetAnnotationProjectionAsync(new(HistoryAnnotationTargetKind.Version, b.Version.VersionId.Value))).IsSuppressed);
    }

    [TestMethod]
    public async Task LockedObsoleteArchiveIsReclaimedAfterRestartWithoutLosingNewChain()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        using (var locked = new FileStream(a.Entry.Locator.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(a)));
            var result = await f.Executor.CommitAsync(prepared);
            Assert.IsTrue(result.Committed);
            Assert.IsTrue(result.CleanupPending);
            Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
            await f.AssertRestoresAsync(b);
        }
        await f.RestartAsync();
        Assert.IsFalse(File.Exists(a.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(b);
    }

    [TestMethod]
    public async Task DiskShortageAndStalePlanDoNotDeleteAnything()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var plan = await f.Planner.PlanAsync(f.Delete(a));
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive, availableSpace: _ => 0);
        await Assert.ThrowsAsync<IOException>(() => executor.PrepareAsync(plan));
        await using var prepared = await f.Executor.PrepareAsync(plan);
        await f.History.Annotations.SetPinAsync(new(HistoryAnnotationTargetKind.Version, a.Version.VersionId.Value), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executor.CommitAsync(prepared));
        Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(a, b);
    }

    [TestMethod]
    public async Task RecoveryKeepsOldBytesWhenACommittedReplacementDisappears()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            step => { if (step == "pack-installed") throw new InvalidOperationException("crash"); });
        string path;
        byte[] replacement;
        await using (var prepared = await executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(a))))
        {
            path = prepared.Mappings.Single().Path;
            replacement = await File.ReadAllBytesAsync(path);
            Assert.IsTrue((await executor.CommitAsync(prepared)).CleanupPending);
        }
        File.Delete(path);
        await f.RestartAsync();
        Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
        Assert.IsTrue(File.Exists(b.Entry.Locator.AbsolutePath));
        await File.WriteAllBytesAsync(path, replacement);
        await f.RestartAsync();
        Assert.IsFalse(File.Exists(a.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(b);
    }

    [TestMethod]
    public async Task CachedRuntimeRecoversThroughTheNextConfigurationOperation()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            step => { if (step == "pack-installed") throw new InvalidOperationException("crash"); });
        await using (var prepared = await executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(a))))
            Assert.IsTrue((await executor.CommitAsync(prepared)).CleanupPending);
        f.History.ObservePendingRecovery();
        Assert.AreNotEqual(FolderRewind.History.Application.HistoryRuntimeHealth.Ready, f.History.Health);
        var restore = new FolderRewind.History.Application.HistoryRestoreService(f.History, f.Engine,
            async token => new FolderRewind.History.Representation.RepresentationEnvironment(
                (await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FolderRewind.History.Application.FileSystemHistoryRestoreMutationBackend());
        await restore.RecoverIncompleteAsync();
        Assert.AreEqual(FolderRewind.History.Application.HistoryRuntimeHealth.Ready, f.History.Health);
        await f.AssertRestoresAsync(b);
        Assert.IsFalse(File.Exists(a.Entry.Locator.AbsolutePath));
    }
}
