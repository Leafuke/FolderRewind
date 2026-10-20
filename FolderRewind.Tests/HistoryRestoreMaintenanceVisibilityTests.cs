using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRestoreMaintenanceVisibilityTests
{
    [TestMethod]
    public async Task ActivePreparationIsNotMaintenanceButAbandonedPreparationIs()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var runtime = new HistoryRuntime(new(new("maintenance-visibility"), new(Path.Combine(root, "history"))));
            await runtime.InitializeReadableAsync();
            var source = SourceId.New();
            var target = Path.Combine(root, "world"); Directory.CreateDirectory(target);
            var workspace = new HistoryWorkspace(runtime.ConfigId, 0, [new(source, null, WorkspaceBaselineRelation.Unknown)]);
            var store = new HistoryRestoreTransactionJournalStore(runtime, new FileSystemHistoryRestoreMutationBackend());
            using (var session = new HistoryRestorePreparationSession(runtime, store, workspace, [new(source, target)], []))
            {
                // A history refresh runs outside the restore's AsyncLocal ownership context.
                Task refresh;
                using (ExecutionContext.SuppressFlow()) refresh = Task.Run(() => runtime.RefreshLocalStateHealthAsync());
                await refresh;
                Assert.IsFalse(runtime.Issues.Any(i => i.Kind == HistoryRuntimeIssueKind.Maintenance));
                session.Finish(); // Leave the record behind, as an interrupted process would.
            }
            await runtime.RefreshLocalStateHealthAsync();
            Assert.IsTrue(runtime.Issues.Any(i => i.Kind == HistoryRuntimeIssueKind.Maintenance));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedCleanupIsNotMaintenanceButFailedCleanupIs(bool invalidRecord)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var runtime = new HistoryRuntime(new(new("cleanup-visibility"), new(Path.Combine(root, "history"))));
            await runtime.InitializeReadableAsync();
            await using (await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(runtime.ConfigId))
            {
                if (invalidRecord)
                {
                    var directory = Path.Combine(runtime.Repository.Paths.TransactionsRoot, HistoryTransactionId.New().ToString());
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "restore-cleanup.json"), "{}");
                    runtime.ScheduleRestoreCleanup();
                }
                else
                {
                    var source = SourceId.New();
                    var target = Path.Combine(root, "world"); Directory.CreateDirectory(target);
                    var workspace = new HistoryWorkspace(runtime.ConfigId, 0, [new(source, null, WorkspaceBaselineRelation.Unknown)]);
                    var store = new HistoryRestoreTransactionJournalStore(runtime, new FileSystemHistoryRestoreMutationBackend());
                    using var session = new HistoryRestorePreparationSession(runtime, store, workspace, [new(source, target)], []);
                    session.Abort();
                }
                await runtime.RefreshLocalStateHealthAsync();
                Assert.IsFalse(runtime.Issues.Any(i => i.Kind == HistoryRuntimeIssueKind.Maintenance));
            }
            await runtime.DrainRestoreCleanupAsync();
            Assert.AreEqual(invalidRecord, runtime.Issues.Any(i => i.Kind == HistoryRuntimeIssueKind.Maintenance));
        }
        finally { Directory.Delete(root, true); }
    }
}
