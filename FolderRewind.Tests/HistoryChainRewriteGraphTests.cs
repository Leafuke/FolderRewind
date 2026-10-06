using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteGraphTests
{
    [TestMethod]
    public async Task ReadyAlternativeAvoidsRecompressionAndPreservesCloudFacts()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "large.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        var alternateId = RepresentationId.New();
        var full = await f.Archive.CreateFullAsync(c.Version, c.StateDirectory, alternateId, Path.Combine(f.Root, "alternative"), default);
        var alternate = new VersionRepresentation(alternateId, c.Version.VersionId, RepresentationKind.CoreFull, "7z", [], MaterializationFidelity.Exact, null, null, null);
        var remote = new StorageReplica(ReplicaId.New(), b.Representation.RepresentationId, ReplicaProviderKind.Cloud,
            "replicas/test.7z", new FileInfo(b.Entry.Locator.AbsolutePath).Length, null, HistoryProvenance.Native("test"));
        var active = new ReplicaLifecycleUpdate(ReplicaLifecycleUpdateId.New(), remote.ReplicaId, [], ReplicaLifecycleState.Active, DateTimeOffset.UtcNow, "test");
        var codec = new HistoryPackCodec();
        await f.History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { alternate, remote, active }.Select(o => codec.CreateObject(o))));
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        await f.History.LocalReplicaCatalogStore.SaveAsync(new(f.Config, catalog.CatalogRevision + 1,
            catalog.Entries.Append(new(alternateId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(full.PayloadPath), DateTimeOffset.UtcNow))), catalog.CatalogRevision);
        await using var prepared = await f.Executor.PrepareAsync(await f.Planner.PlanAsync(f.Delete(b)));
        Assert.AreEqual(0L, prepared.CreatedBytes);
        Assert.AreEqual(alternateId, prepared.Mappings.Single(m => m.PreviousId == c.Representation.RepresentationId).Replacement.RepresentationId);
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        await f.AssertRestoresAsync(a, c, d);
        Assert.AreEqual(b.Representation.RepresentationId, (await f.History.Query.GetRepresentationAsync(c.Representation.RepresentationId))!.DependencyRepresentationIds.Single());
        var lifecycle = await f.History.Query.GetReplicaLifecycleUpdatesAsync(remote.ReplicaId);
        Assert.HasCount(1, lifecycle);
        Assert.AreEqual(ReplicaLifecycleState.Active, lifecycle.Single().State);
    }

    [TestMethod]
    public async Task SecondLocalReplicaPreservesDependencyWithoutRewriting()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var extra = Path.Combine(f.Root, "extra.7z");
        File.Copy(a.Entry.Locator.AbsolutePath, extra);
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        await f.History.LocalReplicaCatalogStore.SaveAsync(new(f.Config, catalog.CatalogRevision + 1,
            catalog.Entries.Append(new(a.Representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(extra), DateTimeOffset.UtcNow))), catalog.CatalogRevision);
        var plan = await f.Planner.PlanAsync(f.Delete(a));
        Assert.HasCount(0, plan.Steps);
        await using var prepared = await f.Executor.PrepareAsync(plan);
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        Assert.IsTrue(File.Exists(extra));
        await f.AssertRestoresAsync(b);
    }

    [TestMethod]
    public async Task CorruptDependencyBlocksEveryBranchBeforeChanges()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        _ = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        File.WriteAllText(b.Entry.Locator.AbsolutePath, "corrupt archive");
        var plan = await f.Planner.PlanAsync(f.Delete(b));
        Assert.IsFalse(plan.CanExecute);
        Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
        Assert.HasCount(3, (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }

    [TestMethod]
    public async Task MultipleConsecutiveTargetsProduceOnlyOneBoundaryArchive()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "large.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        var plan = await f.Planner.PlanAsync(f.Delete(b, c));
        Assert.HasCount(1, plan.Steps);
        await using var prepared = await f.Executor.PrepareAsync(plan);
        Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        await f.AssertRestoresAsync(a, d);
    }
}
