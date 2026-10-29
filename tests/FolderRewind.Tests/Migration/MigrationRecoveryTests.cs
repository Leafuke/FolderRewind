using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MigrationRecoveryTests
{
    private string _root = null!;
    private static string Id() => Guid.NewGuid().ToString("N");
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "MigrationRecovery", Id()); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private ConfigImportTransactionStore Activate(string[] ids, ConfigImportMode mode = ConfigImportMode.LocalRestore)
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, "{\"name\":\"old\"}");
        var store = new ConfigImportTransactionStore(path);
        store.Stage("{\"name\":\"new\"}"u8.ToArray(), _ => null, ids, mode);
        Assert.IsTrue(store.BeginActivation(_ => null)); store.CommitActivation(); return store;
    }
    [TestMethod] public void BackupAndCloudPermissionsAreIndependentAndSurviveRestart()
    {
        var first = Id(); var second = Id(); Activate([first, second]);
        MigrationSetupStateStore.Grant(_root, first, true, false);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, first));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, first));
        Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, second));
        MigrationSetupStateStore.Grant(_root, first, false, true);
        Assert.IsFalse(MigrationSetupStateStore.IsCloudPending(_root, first));
        MigrationSetupStateStore.Revoke(_root, first, false, true);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, first));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, first));
    }
    [TestMethod] public void CorruptProjectPermissionDoesNotBlockOtherProjects()
    {
        var damaged = Id(); var healthy = Id(); Activate([damaged, healthy]);
        MigrationSetupStateStore.Confirm(_root, damaged); MigrationSetupStateStore.Confirm(_root, healthy);
        File.WriteAllText(Path.Combine(_root, "migration", "projects", damaged + ".permissions.json"), "bad json");
        Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, damaged));
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, healthy));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => MigrationSetupStateStore.Validate(_root, damaged));
    }
    [TestMethod] public void AnotherImportCannotReusePermissionsEvenWithIdenticalBytes()
    {
        var id = Id(); var store = Activate([id]); MigrationSetupStateStore.Confirm(_root, id);
        store.Stage("{\"name\":\"new\"}"u8.ToArray(), _ => null, [id]);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, id));
        Assert.IsTrue(store.BeginActivation(_ => null)); store.CommitActivation();
        Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, id));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, id));
    }
    [TestMethod] public void ImportModeAndPreviousConfigEvidenceRollbackTogether()
    {
        var first = Id(); var store = Activate([first]);
        CollectionAssert.AreEqual("{\"name\":\"old\"}"u8.ToArray(), store.ReadPreviousConfig());
        Assert.AreEqual(ConfigImportMode.LocalRestore, store.ActiveMode);
        MigrationSetupStateStore.Grant(_root, first, true, false);
        store.Stage("{\"name\":\"another\"}"u8.ToArray(), _ => null, [Id()], ConfigImportMode.CrossDevice);
        Assert.IsTrue(store.BeginActivation(_ => null));
        Assert.AreEqual(ConfigImportMode.CrossDevice, store.ActiveMode);
        Assert.IsFalse(new ConfigImportTransactionStore(Path.Combine(_root, "config.json")).BeginActivation(_ => null));
        Assert.AreEqual(ConfigImportMode.LocalRestore, store.ActiveMode);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, first));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, first));
    }
    [TestMethod] public void SelectedProjectMergeKeepsOtherProjectsPermissionsAndPreviousConfigEvidence()
    {
        var first = Id(); var second = Id(); var store = Activate([first]); var originalToken = store.PendingToken(first);
        MigrationSetupStateStore.Grant(_root, first, true, false);
        store.Stage("{\"name\":\"merged\"}"u8.ToArray(), _ => null, [second], ConfigImportMode.CrossDevice, preserveOtherProjectSetup: true);
        Assert.IsTrue(store.BeginActivation(_ => null)); store.CommitActivation();
        Assert.AreEqual(originalToken, store.PendingToken(first)); Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, first));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, first)); Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, second));
        Assert.AreEqual(ConfigImportMode.LocalRestore, store.ModeFor(first)); Assert.AreEqual(ConfigImportMode.CrossDevice, store.ModeFor(second));
        CollectionAssert.AreEqual("{\"name\":\"old\"}"u8.ToArray(), store.ReadPreviousConfig(first));
    }
    [TestMethod] public void LegacyRegistryAndConfirmationRemainReadableWithoutInventingOriginalSettings()
    {
        var id = Id(); var store = Activate([id]);
        var registryPath = Path.Combine(_root, "migration", "config-import", "setup.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(registryPath));
        var token = doc.RootElement.GetProperty("importId").GetString()!;
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new { importId = token, configIds = new[] { id } }));
        Assert.IsTrue(store.HasLegacySetup); Assert.IsNull(store.ReadPreviousConfig());
        var confirmations = Path.Combine(_root, "migration", "projects"); Directory.CreateDirectory(confirmations);
        File.WriteAllText(Path.Combine(confirmations, id + ".confirmed"), token);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, id));
        Assert.IsFalse(MigrationSetupStateStore.IsCloudPending(_root, id));
        MigrationSetupStateStore.Grant(_root, id, false, true);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, id), "Adding a cloud decision must preserve a legacy backup confirmation.");
        MigrationSetupStateStore.Revoke(_root, id, false, true);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, id));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, id));
    }
    [TestMethod] public async Task HistoryOnlyInitializationDoesNotStartTasksBeforeConfirmation()
    {
        var id = new HistoryConfigId(Id()); var source = SourceId.New();
        var paths = HistoryRepositoryPaths.ForConfigDirectory(_root, id);
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(id, paths)); await runtime.InitializeAsync();
        runtime.MigrationState.MarkImported();
        Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, id.Value));
        await new HistoryMigrationInitializationService(runtime).InitializeAsync([source]);
        Assert.AreEqual(HistoryRuntimeHealth.Ready, runtime.Health);
        Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, id.Value));
        MigrationSetupStateStore.Grant(_root, id.Value, true, false);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, id.Value));
        Assert.IsTrue(MigrationSetupStateStore.IsCloudPending(_root, id.Value));
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, (await runtime.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).Relation);
    }
    [TestMethod] public async Task LocalInitializationRetainsExactBaselineBranchAndReplicaCatalog()
    {
        var id = new HistoryConfigId(Id()); var source = SourceId.New();
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(id, HistoryRepositoryPaths.ForConfigDirectory(_root, id)));
        await runtime.InitializeAsync();
        var baseline = new WorkspaceSourceBaseline(source, VersionId.New(), WorkspaceBaselineRelation.Exact, BranchId.New(), BranchUpdateId.New(), CheckpointId.New());
        var workspace = new HistoryWorkspace(id, 0, [baseline]);
        await runtime.WorkspaceStore.SaveAsync(workspace, -1); await runtime.LocalReplicaCatalogStore.SaveAsync(new(id, 0, []), -1);
        runtime.MigrationState.MarkImported(); await new HistoryMigrationInitializationService(runtime).InitializeAsync([source]);
        Assert.IsTrue(HistoryWorkspace.StateEquals(workspace, (await runtime.WorkspaceStore.LoadAsync()).Value!));
        Assert.AreEqual(0, (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision);
    }
    [TestMethod] public async Task ControlledRebindResetsOnlyChangedSourceAndPreservesOtherBranch()
    {
        var id = new HistoryConfigId(Id()); var changed = SourceId.New(); var unchanged = SourceId.New();
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(id, HistoryRepositoryPaths.ForConfigDirectory(_root, id)));
        await runtime.InitializeAsync();
        var original = new WorkspaceSourceBaseline(unchanged, VersionId.New(), WorkspaceBaselineRelation.Exact, BranchId.New(), BranchUpdateId.New());
        await runtime.WorkspaceStore.SaveAsync(new(id, 0, [new(changed, VersionId.New(), WorkspaceBaselineRelation.Exact), original]), -1);
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(id, 0, []), -1);
        runtime.MigrationState.MarkImported();
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(id);
        await new HistoryMigrationInitializationService(runtime).InitializeInsideOperationAsync([changed, unchanged], operation, [changed]);
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, workspace.GetSourceState(changed).Relation);
        Assert.IsNull(workspace.GetSourceState(changed).BaseVersionId);
        Assert.AreEqual(original, workspace.GetSourceState(unchanged));
        await new HistoryMigrationInitializationService(runtime).InitializeInsideOperationAsync([changed, unchanged], operation, [changed]);
        Assert.IsTrue(HistoryWorkspace.StateEquals(workspace, (await runtime.WorkspaceStore.LoadAsync()).Value!));
    }
    [TestMethod] public async Task BatchFailureAndCancellationKeepEarlierProjectDecisions()
    {
        var ids = new[] { Id(), Id(), Id() }; Activate(ids);
        var result = await MigrationBatchConfirmation.RunAsync(ids, id => id, (id, _) =>
        {
            if (id == ids[1]) throw new IOException("Injected failure");
            MigrationSetupStateStore.Grant(_root, id, true, false);
            return Task.FromResult(new MigrationResumeResult(id, true, false, ""));
        }, e => e.Message);
        Assert.HasCount(3, result); Assert.AreEqual("Injected failure", result[1].Diagnostic);
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, ids[0]));
        Assert.IsTrue(MigrationSetupStateStore.IsBackupPending(_root, ids[1]));
        Assert.IsFalse(MigrationSetupStateStore.IsBackupPending(_root, ids[2]));
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var canceled = await MigrationBatchConfirmation.RunAsync(ids, id => id, (id, _) =>
        { attempts++; cancellation.Cancel(); return Task.FromResult(new MigrationResumeResult(id, true, false, "")); }, e => e.Message, cancellation.Token);
        Assert.AreEqual(1, attempts); Assert.HasCount(1, canceled);
    }
    [TestMethod] public void OnlyChangedSourceSemanticsLoseTheirLocalBaseline()
    {
        var first = Id(); var second = Id();
        string Config(string a, string b, int scope = 0) => JsonSerializer.Serialize(new { Kind = "core", SourceFolders = new[]
        { new { Id = first, Path = a, SourceScope = new { Mode = scope } }, new { Id = second, Path = b, SourceScope = new { Mode = 0 } } } });
        using var old = JsonDocument.Parse(Config(@"D:\first", @"D:\second"));
        using var unchanged = JsonDocument.Parse(Config(@"d:\FIRST", @"D:\second"));
        Assert.IsEmpty(MigrationRecoveryPolicy.ResetSources(unchanged.RootElement, old.RootElement, true));
        using var changed = JsonDocument.Parse(Config(@"E:\first", @"D:\second"));
        CollectionAssert.AreEqual(new[] { first }, MigrationRecoveryPolicy.ResetSources(changed.RootElement, old.RootElement, true));
        using var scopeChanged = JsonDocument.Parse(Config(@"D:\first", @"D:\second", 1));
        CollectionAssert.AreEqual(new[] { first }, MigrationRecoveryPolicy.ResetSources(scopeChanged.RootElement, old.RootElement, true));
        CollectionAssert.AreEqual(new[] { first, second }, MigrationRecoveryPolicy.ResetSources(unchanged.RootElement, old.RootElement, false));
    }
}
