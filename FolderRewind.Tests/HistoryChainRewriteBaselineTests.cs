using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteBaselineTests
{
    [TestMethod]
    public async Task RewriteMigratesHistoricalCacheAndRebuildNeverReadsLiveSource()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "large.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var files = c.Tree.Files.ToImmutableSortedDictionary(p => p.Key, p => new SourceCaptureFileState(p.Value.Length, p.Value.LastWriteUtc), StringComparer.Ordinal);
        await f.History.CaptureBaselines.SaveAsync(f.Source, new(-1, c.Entry.Locator.AbsolutePath, 2, files,
            c.Version.EffectiveSourceBoundaryFingerprint), c.Version.VersionId, c.Representation);
        await f.History.WorkspaceStore.SaveAsync(new(f.Config, 1,
            [new WorkspaceSourceBaseline(f.Source, c.Version.VersionId, WorkspaceBaselineRelation.Exact)]), 0);
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b)));
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        var migrated = (await f.History.CaptureBaselines.LoadAsync(f.Source))!;
        Assert.AreNotEqual(c.Representation.RepresentationId, migrated.BaseRepresentationId);
        CollectionAssert.AreEqual(files.ToArray(), migrated.FileStates.ToArray());
        Assert.AreEqual(1, migrated.ConsecutiveSmartCaptures);
        Assert.IsTrue(await HistoryCaptureBaselineRepair.IsLocallyAvailableAsync(f.History, migrated, default));
        await f.History.CaptureBaselines.RemoveAsync(f.Source, migrated.Revision);
        File.WriteAllText(Path.Combine(c.StateDirectory, "c.txt"), "live changes must not become baseline");
        var rebuilt = (await HistoryCaptureBaselineRepair.RebuildAsync(f.History, f.Engine, f.Source))!;
        CollectionAssert.AreEqual(files.ToArray(), rebuilt.FileStates.ToArray());
        await f.RestartAsync();
        Assert.IsTrue(await HistoryCaptureBaselineRepair.IsLocallyAvailableAsync(f.History,
            (await f.History.CaptureBaselines.LoadAsync(f.Source))!, default));
    }
}
