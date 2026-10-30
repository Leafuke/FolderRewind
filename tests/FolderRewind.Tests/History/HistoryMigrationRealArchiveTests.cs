using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
public sealed class HistoryMigrationRealArchiveTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmbeddedFullAndDeltaClosureRestoresOnAnEmptyDeviceWithActualArchives(bool encrypted)
    {
        await using var fixture = new HistoryChainRewriteFixture(encrypted); await fixture.InitializeAsync();
        var full = await fixture.AddAsync(null, path => { File.WriteAllText(Path.Combine(path, "base.txt"), "original"); File.WriteAllText(Path.Combine(path, "deleted.txt"), "remove me"); });
        var delta = await fixture.AddAsync(full, path => { File.WriteAllText(Path.Combine(path, "base.txt"), "changed"); File.Delete(Path.Combine(path, "deleted.txt")); File.WriteAllText(Path.Combine(path, "new.txt"), "new"); });
        var bundlePath = Path.Combine(fixture.Root, "migration.frhistory"); var service = new HistoryMigrationBundleService();
        await service.ExportAsync([new(new(fixture.Config, "real archive", [new(fixture.Source, "source", full.StateDirectory)], fixture.History), fixture.BackupRoot, fixture.Archive)],
            bundlePath, "test", null, true);
        var preview = await service.PreviewAsync(bundlePath);
        foreach (var node in fixture.Nodes) File.Delete(node.Entry.Locator.AbsolutePath);
        var configDirectory = Path.Combine(fixture.Root, "fresh-device");
        await new HistoryRepositoryBatchTransferService().ImportAsync(bundlePath, configDirectory, expectedFileSha256: preview.History.FileSha256);
        var relocated = Path.Combine(fixture.Root, "relocated-archives"); await service.InstallPayloadsAsync(bundlePath, preview, fixture.Config, configDirectory, relocated);
        await using var target = new HistoryRuntime(new FileHistoryRepository(fixture.Config, HistoryRepositoryPaths.ForConfigDirectory(configDirectory, fixture.Config)));
        await target.InitializeAsync(); await new HistoryMigrationInitializationService(target).InitializeAsync([fixture.Source]);
        var recovery = new HistoryReplicaRecoveryService(target);
        var candidates = await recovery.InspectAsync(relocated, preview.History.Projects.Single().Migration!.Replicas);
        var result = await recovery.ApplyAsync(candidates, candidates.Candidates, fixture.Archive, false);
        Assert.AreEqual(2, result.RegisteredReplicas); Assert.IsTrue(result.Versions.All(v => v.Readiness == HistoryReadiness.Ready));
        var graph = await target.Query.GetAllRepresentationsAsync();
        var environment = new RepresentationEnvironment((await target.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var destination = Path.Combine(fixture.Root, "actual-restore");
        await fixture.Engine.MaterializeAsync(delta.Representation.RepresentationId, graph, environment, MaterializationFidelity.Exact, destination, default);
        Assert.AreEqual("changed", File.ReadAllText(Path.Combine(destination, "base.txt"))); Assert.AreEqual("new", File.ReadAllText(Path.Combine(destination, "new.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "deleted.txt")));
    }
}
