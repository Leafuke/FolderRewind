using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryRestoreMaintenanceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExecutionCleanupPreservesUnknownOrReplacedRollbackMemberAfterLaterMutation(bool replace)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        try
        {
            HistoryRestoreFileSystemPlanStore.TestFault = point =>
            {
                if (point != "before-cleanup") return;
                var rollback = Directory.GetDirectories(f.Root, "rollback", SearchOption.AllDirectories).Single();
                var path = Path.Combine(rollback, replace ? "a.txt" : "external.txt");
                if (replace) File.Move(path, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(rollback)!)!, "saved-original"));
                File.WriteAllText(path, "foreign rollback content");
                HistoryRestoreFileSystemPlanStore.TestFault = null;
            };
            var result = await f.Restore(targetVolume: true).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
            Assert.IsTrue(result.TargetCommitted, result.Diagnostic);
            await f.History.DrainRestoreCleanupAsync();
            var reason = replace ? "IdentityMismatch" : "UnregisteredObject";
            StringAssert.Contains(f.History.MaintenanceDiagnostic!, "Reason=" + reason);
            StringAssert.Contains(f.History.MaintenanceDiagnostic!, "RelativePath=rollback");
            var state = (await f.History.WorkspaceStore.LoadAsync()).Value!;
            await f.History.WorkspaceStore.SaveAsync(state.WithSourceStates([state.GetSourceState(f.Source) with { RequiresFullCapture = true }]), state.StateRevision);
            File.WriteAllText(Path.Combine(f.Target, "a.txt"), "later world content");
            for (int repeat = 0; repeat < 2; repeat++)
            {
                await f.Restore().RecoverIncompleteAsync();
                HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot);
                Assert.AreEqual("later world content", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
                Assert.AreEqual("foreign rollback content", File.ReadAllText(Directory.GetFiles(f.Root, replace ? "a.txt" : "external.txt", SearchOption.AllDirectories)
                    .Single(path => path.Contains(Path.DirectorySeparatorChar + "rollback" + Path.DirectorySeparatorChar))));
            }
        }
        finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
    }

    [TestMethod]
    public async Task InaccessibleOwnedPreparationBecomesMaintenanceAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("NTFS ACL requires Windows."); return; }
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var store = new HistoryRestoreTransactionJournalStore(f.History, new FileSystemHistoryRestoreMutationBackend());
        string container;
        using (var session = new HistoryRestorePreparationSession(f.History, store, f.Before, [new(f.Source, f.Target)], []))
        {
            File.WriteAllText(Path.Combine(session.CreateStaging(f.Source), "partial"), "owned");
            container = session.Journal.Workspaces.Single().Location.Container;
            session.Finish();
        }
        var directory = new DirectoryInfo(container);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        var saved = original.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User!;
        try
        {
            original.AddAccessRule(new(user, System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, original);
            await store.RecoverIncompleteAsync(default); await f.History.DrainRestoreCleanupAsync();
            HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot);
            Assert.IsTrue(Directory.Exists(container));
            Assert.IsNotNull(f.History.MaintenanceDiagnostic);
            Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
        }
        finally
        {
            var restored = new System.Security.AccessControl.DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(saved, System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, restored);
        }
        f.History.ScheduleRestoreCleanup(); await f.History.DrainRestoreCleanupAsync();
        Assert.IsFalse(Directory.Exists(container));
    }

    [TestMethod]
    public async Task LiveExecutionRejectsChangedLogicalMappingEvenWhenRecordedPhysicalObjectsRemain()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var location = HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), [new(f.Source, f.Target)], []).Single();
        HistoryRestoreWorkspacePlanner.ValidateBinding(location);
        Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.ValidateBinding(location with { LogicalTarget = f.OtherTarget }));
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(f.OtherTarget, "keep.txt")));
    }

    [TestMethod]
    public async Task NewPreparationDoesNotPersistWorkspaceAndCanBeAbandonedAfterWorkspaceChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-preparation-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var history = new HistoryRuntime(new(new("preparation-record"), new(Path.Combine(root, "history"))));
            await history.InitializeAsync();
            var source = SourceId.New(); var target = Path.Combine(root, "world"); Directory.CreateDirectory(target);
            var state = new HistoryWorkspace(history.ConfigId, 0, [new(source, null, WorkspaceBaselineRelation.Unknown)]);
            await history.WorkspaceStore.SaveAsync(state, -1);
            var store = new HistoryRestoreTransactionJournalStore(history, new FileSystemHistoryRestoreMutationBackend());
            HistoryTransactionId transaction;
            using (var session = new HistoryRestorePreparationSession(history, store, state, [new(source, target)], []))
            {
                transaction = session.Journal.TransactionId;
                var staging = session.CreateStaging(source);
                File.WriteAllText(Path.Combine(staging, "partial"), "owned");
                using var json = JsonDocument.Parse(File.ReadAllBytes(store.PreparationPath(transaction)));
                Assert.IsFalse(json.RootElement.TryGetProperty("expectedWorkspace", out _));
                Assert.IsFalse(json.RootElement.TryGetProperty("desiredWorkspace", out _));
                Assert.IsFalse(File.Exists(Path.Combine(store.TransactionDirectory(transaction), "restore-journal.json")));
                session.Finish();
            }
            var next = state.WithSourceStates([state.GetSourceState(source) with { RequiresFullCapture = true }]);
            await history.WorkspaceStore.SaveAsync(next, 0);
            await store.RecoverIncompleteAsync(default); await history.DrainRestoreCleanupAsync();
            Assert.IsTrue(HistoryWorkspace.StateEquals(next, (await history.WorkspaceStore.LoadAsync()).Value!));
            Assert.IsFalse(File.Exists(store.PreparationPath(transaction)));
            Assert.IsEmpty(Directory.GetDirectories(root, ".folderrewind-restore-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("rollback", false, true)]
    [DataRow("rollback-items", true, true)]
    [DataRow("rollback-items", false, false)]
    public async Task PreparationNeverDiscardsAnOriginalRootEvenWhenItsDirectoryIsEmpty(string name, bool hasFile, bool blocks)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var store = new HistoryRestoreTransactionJournalStore(f.History, new FileSystemHistoryRestoreMutationBackend());
        string container;
        using (var session = new HistoryRestorePreparationSession(f.History, store, f.Before, [new(f.Source, f.Target)], []))
        {
            session.CreateStaging(f.Source); container = session.Journal.Workspaces.Single().Location.Container;
            var rollback = Path.Combine(container, name); Directory.CreateDirectory(rollback);
            if (hasFile) File.WriteAllText(Path.Combine(rollback, "original"), "must remain");
            session.Finish();
        }
        if (blocks)
        {
            Assert.Throws<InvalidDataException>(() => HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.RecoverIncompleteAsync(default));
            Assert.IsTrue(Directory.Exists(Path.Combine(container, name)));
        }
        else
        {
            await store.RecoverIncompleteAsync(default); await f.History.DrainRestoreCleanupAsync();
            Assert.IsFalse(Directory.Exists(container));
        }
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
    }

    [TestMethod]
    public async Task DamagedCompleteEnvelopeCannotBypassMutationGateOrHideReadableHistory()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var result = await f.Restore(targetVolume: true).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
            new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        await f.History.DrainRestoreCleanupAsync();
        var path = Directory.GetFiles(f.History.Repository.Paths.TransactionsRoot, "restore-journal.json", SearchOption.AllDirectories)
            .Single(p => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p))!["protocolVersion"]?.GetValue<int>() == 2);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!;
        json["expectedWorkspace"] = null; File.WriteAllText(path, json.ToJsonString());
        await f.History.RefreshLocalStateHealthAsync();
        Assert.IsTrue(f.History.Issues.Any(i => i.Kind == HistoryRuntimeIssueKind.Transaction && i.BlocksMutation));
        Assert.Throws<InvalidDataException>(() => HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot));
        Assert.IsNotEmpty(await f.History.Query.GetAllVersionsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyPreparationCleanupCannotServeAsExecutionProof(bool forgedExecution)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var store = new HistoryRestoreTransactionJournalStore(f.History, new FileSystemHistoryRestoreMutationBackend());
        var state = (await f.History.WorkspaceStore.LoadAsync()).Value!;
        HistoryRestorePreparationRecord record;
        using (var session = new HistoryRestorePreparationSession(f.History, store, state, [new(f.Source, f.Target)], []))
        {
            File.WriteAllText(Path.Combine(session.CreateStaging(f.Source), "partial"), "owned");
            record = session.PreparationRecord;
            session.Finish();
        }
        var cleanup = Path.Combine(store.TransactionDirectory(record.TransactionId), "restore-cleanup.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        if (forgedExecution)
            File.WriteAllBytes(cleanup, JsonSerializer.SerializeToUtf8Bytes(new HistoryRestoreCleanupRecord(2, RestoreCleanupKind.Committed,
                f.History.ConfigId, record.TransactionId, record.Workspaces, null, "invented"), options));
        else
            File.WriteAllBytes(cleanup, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, record.TransactionId, record.Workspaces }, options));
        store.RetryDeferredCleanup();
        Assert.AreEqual(forgedExecution, File.Exists(cleanup));
        Assert.AreEqual(forgedExecution, Directory.Exists(record.Workspaces.Single().Location.Container));
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
        if (forgedExecution) StringAssert.Contains(f.History.MaintenanceDiagnostic!, "outcome proof");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredCleanupSurvivesLaterWorkspaceAndTargetChanges(bool rollback)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        try
        {
            HistoryRestoreFileSystemPlanStore.TestFault = point =>
            {
                if (point == "before-cleanup" || rollback && point == "after-move-0") throw new IOException("maintenance failure");
            };
            var result = await f.Restore(targetVolume: true).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(rollback ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            await f.History.DrainRestoreCleanupAsync();
            Assert.IsNotEmpty(Directory.GetFiles(f.History.Repository.Paths.TransactionsRoot, "restore-cleanup.json", SearchOption.AllDirectories));
            HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot);
            var state = (await f.History.WorkspaceStore.LoadAsync()).Value!;
            var next = state.WithSourceStates([state.GetSourceState(f.Source) with { RequiresFullCapture = true }]);
            await f.History.WorkspaceStore.SaveAsync(next, state.StateRevision);
            var userFile = Path.Combine(f.Target, "a.txt");
            File.WriteAllText(userFile, "new user content");
            var identity = WindowsRestoreFileSystem.Identity(userFile);
            HistoryRestoreFileSystemPlanStore.TestFault = null;
            f.History.ScheduleRestoreCleanup(); await f.History.DrainRestoreCleanupAsync();
            Assert.AreEqual("new user content", File.ReadAllText(userFile));
            Assert.AreEqual(identity, WindowsRestoreFileSystem.Identity(userFile));
            Assert.IsTrue(HistoryWorkspace.StateEquals(next, (await f.History.WorkspaceStore.LoadAsync()).Value!));
            Assert.IsEmpty(Directory.GetFiles(f.History.Repository.Paths.TransactionsRoot, "restore-cleanup.json", SearchOption.AllDirectories));
            Assert.IsEmpty(Directory.GetDirectories(f.Root, ".folderrewind-restore-*"));
            Assert.AreEqual(HistoryRuntimeHealth.Ready, f.History.Health);
        }
        finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
    }
}
