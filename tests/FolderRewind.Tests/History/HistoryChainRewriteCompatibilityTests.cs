using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Retention;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteCompatibilityTests
{
    [TestMethod]
    public async Task EncryptedChainRequiresCredentialAndReplacementStaysEncrypted()
    {
        await using var f = new HistoryChainRewriteFixture(encrypted: true);
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "large.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var password = f.Password;
        f.Password = null;
        Assert.IsFalse((await f.Planner.PlanAsync(f.Delete(b))).CanExecute);
        Assert.IsTrue(File.Exists(b.Entry.Locator.AbsolutePath));
        f.Password = password;
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b)));
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        var replacement = prepared.Mappings.Single();
        f.Password = "wrong-password";
        Assert.IsFalse((await f.Archive.VerifyAsync(replacement.Replacement, replacement.Path, default)).Success);
        f.Password = password;
        await f.AssertRestoresAsync(c);
    }

    [TestMethod]
    public async Task UnknownLegacyBoundaryIsNotRewritten()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var legacy = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "legacy.txt"), "legacy"),
            provenance: new(HistoryOrigin.LegacyMigration, "test", "old history"));
        Assert.IsFalse((await f.Planner.PlanAsync(f.Delete(legacy))).CanExecute);
        Assert.IsTrue(File.Exists(legacy.Entry.Locator.AbsolutePath));
    }

    [TestMethod]
    public async Task NextPartialIncrementalUsesMigratedRepresentationAndInheritsOutsideFiles()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "outside.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "inside.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "inside.txt"), "C"), scope: CaptureScope.PartialSource);
        await f.History.WorkspaceStore.SaveAsync(new(f.Config, 1,
            [new WorkspaceSourceBaseline(f.Source, c.Version.VersionId, WorkspaceBaselineRelation.Exact)]), 0);
        var baseline = (await HistoryCaptureBaselineRepair.RebuildAsync(f.History, f.Engine, f.Source))!;
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b)));
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        var migrated = (await f.History.CaptureBaselines.LoadAsync(f.Source))!;
        CollectionAssert.AreEqual(baseline.FileStates.ToArray(), migrated.FileStates.ToArray());
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        var parent = c with { Representation = graph.Single(r => r.RepresentationId == migrated.BaseRepresentationId),
            Entry = catalog.Entries.First(e => e.RepresentationId == migrated.BaseRepresentationId) };
        var d = await f.AddAsync(parent, p => File.WriteAllText(Path.Combine(p, "inside.txt"), "D"), scope: CaptureScope.PartialSource);
        Assert.AreEqual(migrated.BaseRepresentationId, d.Representation.DependencyRepresentationIds.Single());
        Assert.IsTrue(d.Tree.Files.ContainsKey("outside.bin"));
        await f.AssertRestoresAsync(c, d);
    }

    [TestMethod]
    public async Task LongUnicodePathsAndEmptyDirectoriesRoundTripThroughNewFullRoot()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var relative = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('长', 35), 6));
        var a = await f.AddAsync(null, p =>
        {
            Directory.CreateDirectory(Path.Combine(p, relative));
            File.WriteAllText(Path.Combine(p, relative, "存档.txt"), "archive");
            Directory.CreateDirectory(Path.Combine(p, "empty"));
        });
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, relative, "存档.txt"), "changed"));
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(a)));
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        await f.AssertRestoresAsync(b);
    }
}
