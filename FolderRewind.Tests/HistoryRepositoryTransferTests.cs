using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.History.LocalState;
using System.Collections.Immutable;
using System.IO.Compression;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRepositoryTransferTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindTransferTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task MetadataTransferImportsAsIdempotentPackUnionForManifestConfigIdentity()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var source = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "source"))));
        await source.InitializeAsync();
        var version = new SourceVersion(
            VersionId.New(), configId, SourceId.New(), [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot("source", "source"), null, HistoryProvenance.Native("test"));
        var codec = new HistoryPackCodec();
        await source.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version)]));
        var transferPath = Path.Combine(_root, "history.frhistory");
        var configDirectory = Path.Combine(_root, "target-config");
        var transfer = new HistoryRepositoryTransferService();

        await transfer.ExportAsync(source, transferPath);
        var first = await transfer.ImportAsync(transferPath, configDirectory);
        var second = await transfer.ImportAsync(transferPath, configDirectory);

        Assert.AreEqual(configId, first.ConfigId);
        Assert.AreEqual(1, first.InstalledPacks);
        Assert.IsTrue(first.ImportedAsOrphanRepository);
        Assert.AreEqual(0, second.InstalledPacks);
        Assert.AreEqual(1, second.DuplicatePacks);
        await using var target = new HistoryRuntime(new FileHistoryRepository(configId,
            HistoryRepositoryPaths.ForConfigDirectory(configDirectory, configId)));
        await target.InitializeAsync();
        Assert.AreEqual(HistoryRuntimeHealth.LocalStateInitializationRequired, target.Health);
        await new HistoryMigrationInitializationService(target).InitializeAsync([version.SourceId]);
        Assert.AreEqual(HistoryRuntimeHealth.Ready, target.Health);
        var baseline = (await target.WorkspaceStore.LoadAsync()).Value!.GetSourceState(version.SourceId);
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, baseline.Relation);
        Assert.IsNull(baseline.BaseVersionId);
        Assert.IsNull(baseline.ActiveBranchId);
        Assert.IsEmpty((await target.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }

    [TestMethod]
    public async Task BatchTransferIncludesAllProjectsAndAssociatesUnloadedMatchingConfiguration()
    {
        var firstId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var secondId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var first = await RuntimeAsync(firstId, "first");
        await using var second = await RuntimeAsync(secondId, "second");
        await AddVersionAsync(first); await AddVersionAsync(second);
        var file = Path.Combine(_root, "batch.frhistory");
        var target = Path.Combine(_root, "target");
        var service = new HistoryRepositoryBatchTransferService(configExists: id => id == firstId);
        await service.ExportAsync([new(firstId, "first", [], first), new(secondId, "second", [], second)], file, "1.9.test");
        var preview = await service.PreviewAsync(file);
        Assert.AreEqual(2, preview.FormatVersion);
        Assert.HasCount(2, preview.Projects);
        Assert.IsTrue(preview.Projects.All(p => p.VersionCount == 1 && p.Sources.Length == 1));
        var results = (await service.ImportAsync(file, target)).Projects;
        Assert.IsFalse(results.Single(p => p.ConfigId == firstId).ImportedAsOrphanRepository);
        Assert.IsTrue(results.Single(p => p.ConfigId == secondId).ImportedAsOrphanRepository);
        Assert.IsTrue(results.All(p => p.CommitStatus == HistoryTransferCommitStatus.Committed));
        Assert.IsTrue((await service.ImportAsync(file, target)).Projects.All(p => p.InstalledPacks == 0 && p.DuplicatePacks == 1));
    }

    [TestMethod]
    public async Task BatchFailureDoesNotUndoOtherProjectAndRetryIsSafe()
    {
        var a = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var b = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var first = await RuntimeAsync(a, "a");
        await using var second = await RuntimeAsync(b, "b");
        var packId = PackId.New();
        await AddVersionAsync(first); await AddVersionAsync(second, packId);
        var file = Path.Combine(_root, "conflict.frhistory");
        var directory = Path.Combine(_root, "conflicting-target");
        await using var conflicting = new HistoryRuntime(new FileHistoryRepository(b,
            HistoryRepositoryPaths.ForConfigDirectory(directory, b)));
        await conflicting.InitializeAsync(); await AddVersionAsync(conflicting, packId);
        var service = new HistoryRepositoryBatchTransferService();
        await service.ExportAsync([new(a, "a", [], first), new(b, "b", [], second)], file, "test");
        var results = (await service.ImportAsync(file, directory)).Projects;
        Assert.AreEqual(HistoryTransferCommitStatus.Committed, results[0].CommitStatus);
        Assert.AreEqual(HistoryTransferCommitStatus.Failed, results[1].CommitStatus);
        var retry = (await service.ImportAsync(file, directory, [a])).Projects.Single();
        Assert.AreEqual(0, retry.InstalledPacks); Assert.AreEqual(1, retry.DuplicatePacks);
        Assert.HasCount(1, await conflicting.Repository.ReadAllPacksAsync());
        Assert.IsNull(conflicting.MigrationState.Load(), "Rejected Pack identity conflicts must not relabel missing local state as migration setup.");
    }

    [TestMethod]
    public async Task InitializationResumesItsTwoFilesButNeverOverwritesCorruptState()
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var source = SourceId.New();
        await using var runtime = await RuntimeAsync(id, "local-init");
        runtime.MigrationState.MarkImported();
        var workspace = new HistoryWorkspace(id, 0, [new(source, null, WorkspaceBaselineRelation.Unknown)]);
        runtime.MigrationState.Prepare([HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(workspace, -1),
            HistoryLocalStateJournalRecovery.CreateCatalogIntent(new(id, 0, []), -1)]);
        await runtime.WorkspaceStore.SaveAsync(workspace, -1); // Simulate interruption between files.
        await new HistoryMigrationInitializationService(runtime).InitializeAsync([source]);
        Assert.IsNotNull((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value);
        Assert.IsFalse(runtime.MigrationState.Load()!.InitializationPending);
        var workspacePath = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "workspace.json");
        File.WriteAllText(workspacePath, "damaged");
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(() =>
            new HistoryMigrationInitializationService(runtime).InitializeAsync([source]));
        Assert.AreEqual("damaged", File.ReadAllText(workspacePath));
    }

    [TestMethod]
    public async Task DuplicateZipEntriesAreRejectedBeforeTargetCreation()
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = await RuntimeAsync(id, "zip");
        var file = Path.Combine(_root, "duplicate.frhistory");
        var service = new HistoryRepositoryBatchTransferService();
        await service.ExportAsync([new(id, "zip", [], runtime)], file, "test");
        using (var archive = ZipFile.Open(file, ZipArchiveMode.Update))
            using (var output = new StreamWriter(archive.CreateEntry("manifest.json").Open())) output.Write("{}");
        var destination = Path.Combine(_root, "never-created");
        await Assert.ThrowsExactlyAsync<HistoryIntegrityConflictException>(() => service.ImportAsync(file, destination));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task PreviewCannotAuthorizeAChangedBundle()
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = await RuntimeAsync(id, "changing-bundle");
        var path = Path.Combine(_root, "changing.frhistory");
        var service = new HistoryRepositoryBatchTransferService();
        await service.ExportAsync([new(id, "source", [], runtime)], path, "test");
        var preview = await service.PreviewAsync(path);
        await AddVersionAsync(runtime);
        await service.ExportAsync([new(id, "source", [], runtime)], path, "test");
        var destination = Path.Combine(_root, "unchanged-target");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportAsync(path, destination,
            expectedFileSha256: preview.FileSha256));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task ImportIntoLoadedRuntimePreservesValidLocalState()
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var source = await RuntimeAsync(id, "preserve-source");
        await AddVersionAsync(source);
        var directory = Path.Combine(_root, "preserve-config");
        await using var target = new HistoryRuntime(new FileHistoryRepository(id, HistoryRepositoryPaths.ForConfigDirectory(directory, id)));
        await target.InitializeAsync();
        var sourceId = SourceId.New();
        var workspace = new HistoryWorkspace(id, 0, [new(sourceId, null, WorkspaceBaselineRelation.Unknown)]);
        await target.WorkspaceStore.SaveAsync(workspace, -1);
        await target.LocalReplicaCatalogStore.SaveAsync(new(id, 0, []), -1);
        var path = Path.Combine(_root, "preserve.frhistory");
        await new HistoryRepositoryTransferService().ExportAsync(source, path);
        var service = new HistoryRepositoryBatchTransferService(_ => target, _ => true);
        var first = (await service.ImportAsync(path, directory)).Projects.Single();
        Assert.AreEqual(HistoryTransferLocalStateStatus.Ready, first.LocalStateStatus);
        Assert.IsTrue(HistoryWorkspace.StateEquals(workspace, (await target.WorkspaceStore.LoadAsync()).Value!));
        Assert.AreEqual(0, (await target.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision);
        Assert.IsFalse(target.MigrationState.Load()!.InitializationPending);
        Assert.AreEqual(1, (await service.ImportAsync(path, directory)).Projects.Single().DuplicatePacks);
        Assert.HasCount(1, await target.Query.GetAllVersionsAsync());
    }

    private async Task<HistoryRuntime> RuntimeAsync(HistoryConfigId id, string name)
    {
        var runtime = new HistoryRuntime(new FileHistoryRepository(id, new(Path.Combine(_root, name))));
        await runtime.InitializeAsync(); return runtime;
    }
    private static async Task AddVersionAsync(HistoryRuntime runtime, PackId? id = null)
    {
        var version = new SourceVersion(VersionId.New(), runtime.ConfigId, SourceId.New(), [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [], new("source", "source"), null, HistoryProvenance.Native("test"));
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new(id ?? PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(version)]));
    }
}
