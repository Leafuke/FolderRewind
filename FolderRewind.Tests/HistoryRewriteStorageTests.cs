using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Retention;
using System.Collections.Immutable;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRewriteStorageTests
{
    [TestMethod]
    public async Task NewPayloadAndWorkStayInConfiguredDestinationAndCancelOnlyRemovesOwnedTrees()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var destination = Path.Combine(f.Root, "chosen-backup-directory");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "user-file.txt");
        File.WriteAllText(sentinel, "keep");
        var plan = await f.Planner.PlanAsync(f.Delete(a) with { BackupRoot = destination });
        var paths = new HistoryChainRewriteJournalStore(f.History, backupRoot: destination);
        string output;
        await using (var prepared = await f.Executor.PrepareAsync(plan))
        {
            output = prepared.Mappings.Single().Path;
            Assert.IsTrue(HistoryRewriteStoragePaths.IsWithin(output, destination));
            Assert.IsTrue(Directory.Exists(paths.WorkRoot(plan.OperationId)));
            Assert.IsFalse(Directory.Exists(Path.Combine(f.History.Repository.Paths.RepositoryRoot, "payloads")));
        }
        Assert.IsFalse(File.Exists(output));
        Assert.IsFalse(Directory.Exists(paths.WorkRoot(plan.OperationId)));
        Assert.AreEqual("keep", File.ReadAllText(sentinel));
        await f.AssertRestoresAsync(a, b);
    }

    [TestMethod]
    public async Task RecoveryUsesJournalDestinationEvenIfConfigurationLaterChanges()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var destination = Path.Combine(f.Root, "old-destination");
        var plan = await f.Planner.PlanAsync(f.Delete(a) with { BackupRoot = destination });
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            stage => { if (stage == "before-reclamation") throw new IOException("injected interruption"); });
        string payload;
        await using (var prepared = await executor.PrepareAsync(plan))
        {
            payload = prepared.Mappings.Single().Path;
            Assert.IsTrue((await executor.CommitAsync(prepared)).CleanupPending);
        }
        var journal = JsonSerializer.Deserialize<HistoryChainRewriteJournal>(File.ReadAllText(
            Path.Combine(f.History.Repository.Paths.LocalStateRoot, "chain-rewrites", plan.OperationId + ".json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var unrelated = Path.Combine(f.Root, "new-destination");
        var otherStore = new HistoryChainRewriteJournalStore(f.History, backupRoot: unrelated);
        Directory.CreateDirectory(otherStore.WorkRoot(plan.OperationId));
        var sentinel = Path.Combine(otherStore.WorkRoot(plan.OperationId), "keep.txt");
        File.WriteAllText(sentinel, "other operation location");
        Assert.IsTrue(await otherStore.FinishAsync(journal));
        Assert.IsTrue(File.Exists(sentinel));
        Assert.IsTrue(File.Exists(payload));
        Assert.IsFalse(Directory.Exists(new HistoryChainRewriteJournalStore(f.History, backupRoot: destination).WorkRoot(plan.OperationId)));
        await f.RestartAsync();
        await f.AssertRestoresAsync(b);
    }

    [TestMethod]
    public async Task OldJournalWithoutDestinationStillCleansOnlyLegacyStaging()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var id = HistoryTransactionId.New();
        var legacy = new HistoryChainRewriteJournalStore(f.History);
        Directory.CreateDirectory(legacy.WorkRoot(id));
        Directory.CreateDirectory(legacy.PayloadRoot(id));
        File.WriteAllText(Path.Combine(legacy.PayloadRoot(id), "staged.7z"), "not committed");
        var json = JsonSerializer.Serialize(new { operationId = id, packId = (string?)null, stateApplied = false,
            complete = false, retiredFiles = Array.Empty<object>(), mappings = Array.Empty<object>(), verifiedFiles = Array.Empty<object>() });
        var old = JsonSerializer.Deserialize<HistoryChainRewriteJournal>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var current = new HistoryChainRewriteJournalStore(f.History, backupRoot: f.BackupRoot);
        Directory.CreateDirectory(current.PayloadRoot(id));
        var sentinel = Path.Combine(current.PayloadRoot(id), "keep.7z");
        File.WriteAllText(sentinel, "new storage must not be touched");
        Assert.IsTrue(await current.FinishAsync(old));
        Assert.IsFalse(Directory.Exists(legacy.PayloadRoot(id)));
        Assert.IsFalse(Directory.Exists(legacy.WorkRoot(id)));
        Assert.IsTrue(File.Exists(sentinel));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyPayloadRelocationPreservesBaselineAndRestoresAfterCommitOrRecovery(bool interrupt)
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "base.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        a = await PutInLegacyStorageAsync(f, a);
        d = await PutInLegacyStorageAsync(f, d);
        var files = d.Tree.Files.ToImmutableSortedDictionary(p => p.Key,
            p => new SourceCaptureFileState(p.Value.Length, p.Value.LastWriteUtc), StringComparer.Ordinal);
        await f.History.CaptureBaselines.SaveAsync(f.Source, new(-1, d.Entry.Locator.AbsolutePath, 3, files, d.Version.EffectiveSourceBoundaryFingerprint),
            d.Version.VersionId, d.Representation);
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            stage => { if (interrupt && stage == "pack-installed") throw new IOException("injected interruption"); });
        var plan = await f.Planner.PlanAsync(f.Delete(b));
        await using (var prepared = await executor.PrepareAsync(plan))
        {
            Assert.IsTrue(prepared.Mappings.All(m => HistoryRewriteStoragePaths.IsWithin(m.Path, f.BackupRoot)));
            Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
            Assert.IsTrue(File.Exists(d.Entry.Locator.AbsolutePath));
            var result = await executor.CommitAsync(prepared);
            Assert.IsTrue(result.Committed, result.Diagnostic);
            Assert.AreEqual(interrupt, result.CleanupPending);
        }
        await f.RestartAsync();
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        Assert.IsTrue(catalog.Entries.All(e => HistoryRewriteStoragePaths.IsWithin(e.Locator.AbsolutePath, f.BackupRoot)));
        Assert.IsFalse(File.Exists(a.Entry.Locator.AbsolutePath));
        Assert.IsFalse(File.Exists(d.Entry.Locator.AbsolutePath));
        var baseline = (await f.History.CaptureBaselines.LoadAsync(f.Source))!;
        Assert.IsTrue(HistoryRewriteStoragePaths.IsWithin(baseline.PayloadPath, f.BackupRoot));
        CollectionAssert.AreEqual(files.ToArray(), baseline.FileStates.ToArray());
        await f.AssertRestoresAsync(a, c, d);
    }

    [TestMethod]
    public async Task MigrationFailureAndCancellationKeepOriginalBytesAndCatalog()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        a = await PutInLegacyStorageAsync(f, a);
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        var plan = await f.Planner.PlanAsync(f.Delete(b));
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            stage => { if (stage == "payload-relocated") throw new IOException("copy failed"); });
        await Assert.ThrowsAsync<IOException>(() => executor.PrepareAsync(plan));
        await using (var prepared = await f.Executor.PrepareAsync(plan))
            Assert.HasCount(1, prepared.Mappings);
        Assert.AreEqual(catalog.CatalogRevision, (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision);
        Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
        await f.AssertRestoresAsync(a, b);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("relative-directory")]
    public async Task MissingOrRelativeDestinationNeverFallsBackToApplicationData(string? destination)
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Planner.PlanAsync(f.Delete(a) with { BackupRoot = destination }));
        Assert.IsTrue(File.Exists(a.Entry.Locator.AbsolutePath));
    }

    [TestMethod]
    public async Task UnwritableDestinationNeverCreatesApplicationDataPayloads()
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "a.txt"), "A"));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var destination = Path.Combine(f.Root, "blocked-by-file");
        File.WriteAllText(destination, "keep this user file");
        var plan = await f.Planner.PlanAsync(f.Delete(a) with { BackupRoot = destination });
        await Assert.ThrowsAsync<IOException>(() => f.Executor.PrepareAsync(plan));
        Assert.AreEqual("keep this user file", File.ReadAllText(destination));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.History.Repository.Paths.RepositoryRoot, "payloads")));
        await f.AssertRestoresAsync(a, b);
    }

    internal static async Task<HistoryChainRewriteFixture.Node> PutInLegacyStorageAsync(HistoryChainRewriteFixture f, HistoryChainRewriteFixture.Node node)
    {
        var path = Path.Combine(f.History.Repository.Paths.RepositoryRoot, "payloads", "rewrite-" + HistoryTransactionId.New(),
            node.Representation.RepresentationId.ToString(), "payload.7z");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Move(node.Entry.Locator.AbsolutePath, path);
        var replacement = new LocalReplicaCatalogEntry(node.Entry.RepresentationId, node.Entry.LocalReplicaId,
            LocalReplicaLocator.ControlledAbsolute(path), node.Entry.RegisteredAtUtc);
        var catalog = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        await f.History.LocalReplicaCatalogStore.SaveAsync(new(f.Config, catalog.CatalogRevision + 1,
            catalog.Entries.Select(e => e.LocalReplicaId == node.Entry.LocalReplicaId ? replacement : e)), catalog.CatalogRevision);
        return node with { Entry = replacement };
    }
}
