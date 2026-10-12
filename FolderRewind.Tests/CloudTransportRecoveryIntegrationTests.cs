using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudTransportRecoveryIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyDeviceRestoresIncrementalClosureUsingRealRcloneAndArchives(bool encrypted)
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE to run the real transport recovery test.");
        await using var fixture = new HistoryChainRewriteFixture(encrypted);
        await fixture.InitializeAsync();
        var full = await fixture.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "base.txt"), "base"));
        var delta = await fixture.AddAsync(full, p => { File.WriteAllText(Path.Combine(p, "base.txt"), "changed"); File.WriteAllText(Path.Combine(p, "new.txt"), "new"); });
        var cloud = Path.Combine(fixture.Root, "cloud"); Directory.CreateDirectory(cloud);
        var config = Path.Combine(fixture.Root, "transport.conf"); File.WriteAllText(config, "[old_name]\ntype = local\n[new_name]\ntype = local\n");
        var tasks = Path.Combine(fixture.Root, "tasks");
        using (var connection = new RcloneExecutionContext(executable, config, cloud, "old_name:repository", tasks))
        {
            var transport = new RcloneNativeHistoryTransport(connection);
            var metadata = new HistoryMetadataSyncService(fixture.History, transport);
            var result = await new HistoryCloudBackupService(fixture.History, transport, token => metadata.SyncAsync(token))
                .UploadClosureAsync([delta.Representation.RepresentationId], (id, _) => Task.FromResult<string?>(fixture.Nodes.First(n => n.Representation.RepresentationId == id).Entry.Locator.AbsolutePath));
            Assert.IsTrue(result.Complete);
            Assert.HasCount(2, result.Items);
        }
        await using var fresh = new HistoryRuntime(new FileHistoryRepository(fixture.Config, new(Path.Combine(fixture.Root, "fresh-device"))));
        await fresh.InitializeAsync();
        using var recovery = new RcloneExecutionContext(executable, config, cloud, "new_name:repository", tasks);
        var readOnly = new RcloneNativeHistoryTransport(recovery);
        var pulled = await new HistoryMetadataSyncService(fresh, readOnly).PullAsync();
        Assert.IsTrue(pulled.Succeeded);
        var downloader = new HistoryReplicaSyncService(fresh, readOnly, _ => throw new InvalidOperationException("Recovery must be read only."), new NoRetirement());
        foreach (var representation in await fresh.Query.GetAllRepresentationsAsync())
        {
            var replica = (await fresh.Query.GetStorageReplicasAsync(representation.RepresentationId)).Single();
            var manifest = await readOnly.ReadManifestAsync(replica.ReplicaId, default);
            Assert.IsNotNull(manifest);
            var result = await downloader.DownloadAsync(manifest, Path.Combine(fixture.Root, "downloads", representation.RepresentationId + ".7z"));
            Assert.AreEqual(HistoryReplicaOperationStatus.Succeeded, result.Status);
        }
        var graph = await fresh.Query.GetAllRepresentationsAsync();
        var catalog = (await fresh.LocalReplicaCatalogStore.LoadAsync()).Value!;
        var destination = Path.Combine(fixture.Root, "recovered");
        await fixture.Engine.MaterializeAsync(delta.Representation.RepresentationId, graph,
            new RepresentationEnvironment(catalog.Entries, [], []), MaterializationFidelity.Exact, destination, default);
        Assert.AreEqual("changed", File.ReadAllText(Path.Combine(destination, "base.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(destination, "new.txt")));
        if (encrypted)
        {
            var missingPassword = new SevenZipArchiveProcessBackend(() => SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z")),
                () => null, false, ".restore-marker");
            var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(missingPassword), new SmartDeltaRepresentationHandler(missingPassword)]);
            var blocked = await engine.AssessVersionAsync(delta.Representation.VersionId, graph,
                new RepresentationEnvironment(catalog.Entries, [], []), AssessmentDepth.Deep, MaterializationFidelity.Exact, default);
            Assert.AreNotEqual(HistoryReadiness.Ready, blocked.Readiness);
        }
    }
    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    {
        public Task EnsureRetirementSafeAsync(StorageReplica replica, bool releaseVersion, CancellationToken token) => throw new InvalidOperationException();
    }

    [TestMethod]
    public async Task CompletePackEnumerationExceedsTheInteractiveBrowseBudget()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindPackEnumeration", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "transport.conf"); File.WriteAllText(config, "[test]\ntype = local\n");
            var id = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            var packs = Enumerable.Range(0, 3500).Select(_ => PackId.New()).ToArray();
            foreach (var pack in packs)
            {
                var directory = Path.Combine(root, "remote", "_folderrewind", "history", HistoryRepositoryPaths.EncodeConfigPathSegment(id), "packs", pack.ToString()[..2]);
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, pack + ".frpack"), []);
            }
            using var connection = new RcloneExecutionContext(executable, config, root, "test:remote", Path.Combine(root, "tasks"));
            var listed = await new RcloneNativeHistoryTransport(connection).ListPacksAsync(id, default);
            CollectionAssert.AreEquivalent(packs, listed.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
}
