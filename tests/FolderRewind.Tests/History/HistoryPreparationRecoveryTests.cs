using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryPreparationRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AbandonedPreparationPreservesNewWorkspaceAndUnregisteredContents(bool contradictoryProgress)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-preparing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var runtime = new HistoryRuntime(new(new("preparation-recovery"), new(Path.Combine(root, "history"))));
            await runtime.InitializeReadableAsync();
            var source = SourceId.New();
            var target = Path.Combine(root, "world"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "original"), "keep");
            var old = new HistoryWorkspace(runtime.ConfigId, 0, [new(source, null, WorkspaceBaselineRelation.Unknown)]);
            await runtime.WorkspaceStore.SaveAsync(old, -1);
            var store = new HistoryRestoreTransactionJournalStore(runtime, new FileSystemHistoryRestoreMutationBackend());
            HistoryTransactionId transaction;
            string container;
            using (var session = new HistoryRestorePreparationSession(runtime, store, old, [new(source, target)], []))
            {
                var staging = session.CreateStaging(source);
                File.WriteAllText(Path.Combine(staging, "partially-extracted"), "not registered in KnownObjects");
                transaction = session.Journal.TransactionId;
                container = session.Journal.Workspaces[0].Location.Container;
                session.Finish(); // Simulate interruption without Abort.
            }
            var updated = old.WithSourceStates([old.GetSourceState(source) with { RequiresFullCapture = true }]);
            await runtime.WorkspaceStore.SaveAsync(updated, old.StateRevision);
            var codec = new HistoryPackCodec();
            var version = new SourceVersion(VersionId.New(), runtime.ConfigId, source, [], DateTimeOffset.UtcNow, null,
                CaptureScope.FullSource, CaptureOutcome.Captured, [], new("world", target), null, HistoryProvenance.Native("test"));
            await runtime.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(version)]));
            if (contradictoryProgress) File.WriteAllBytes(Path.Combine(store.TransactionDirectory(transaction), "restore-progress.bin"), [1]);
            for (int i = 0; i < 2; i++)
            {
                if (contradictoryProgress)
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.RecoverIncompleteAsync(default));
                else
                {
                    await store.RecoverIncompleteAsync(default);
                    await runtime.DrainRestoreCleanupAsync();
                }
            }
            Assert.IsTrue(HistoryWorkspace.StateEquals(updated, (await runtime.WorkspaceStore.LoadAsync()).Value!));
            Assert.HasCount(1, await runtime.Repository.ReadAllPacksAsync());
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "original")));
            Assert.AreEqual(contradictoryProgress, Directory.Exists(container));
            if (!contradictoryProgress) HistoryRestoreTransactionJournalStore.RequireRecovered(runtime.Repository.Paths.TransactionsRoot);
        }
        finally { Directory.Delete(root, true); }
    }
}
