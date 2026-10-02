using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeArchiveRecoveryTests
{
    [TestMethod]
    [DataRow("before-pack")]
    [DataRow("after-pack")]
    [DataRow("cleanup-failure")]
    [DataRow("cleanup-cancel")]
    public async Task RealMergeJournalRecoversByPackDurabilityWithoutDuplicateFacts(string failure)
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        var packCount = (await fixture.History.Repository.ReadAllPacksAsync()).Count;
        var versionCount = (await fixture.History.Query.GetAllVersionsAsync()).Count;
        var prepared = await fixture.Builder().BuildAsync(fixture.Session);
        using var cancellation = new CancellationTokenSource();
        var backend = new FaultBackend(new FileSystemHistoryRestoreMutationBackend(), failure, cancellation);
        FileStream? catalogLock = null;
        var restore = fixture.Restore(backend, async (pack, token) =>
        {
            Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(fixture.Target, "b.txt")), "Fault must follow real filesystem Apply.");
            if (failure == "before-pack") throw new IOException("Injected failure before Pack publication");
            await fixture.History.Repository.CommitAsync(pack, cancellationToken: token);
            if (failure == "after-pack")
            {
                catalogLock = new FileStream(Path.Combine(fixture.History.Repository.Paths.LocalStateRoot, "replicas.json"),
                    FileMode.Open, FileAccess.Read, FileShare.None);
                throw new IOException("Injected interruption after durable Pack");
            }
        });
        HistoryRestoreResult result;
        try
        {
            result = await new HistoryMergeApplyService(fixture.History, restore, fixture.Builder(restore))
                .ApplyAsync(fixture.Session, cancellation.Token, prepared);
        }
        finally { catalogLock?.Dispose(); }
        var expectedStatus = failure switch
        {
            "before-pack" => HistoryRestoreStatus.MutationFailedRecoveryRequired,
            "after-pack" => HistoryRestoreStatus.CommittedRecoveryRequired,
            _ => HistoryRestoreStatus.CommittedWithPostActionWarning
        };
        Assert.AreEqual(expectedStatus, result.Status, result.Diagnostic);
        Assert.AreEqual(failure != "before-pack", result.TargetCommitted);
        Assert.AreEqual(failure != "before-pack", File.Exists(fixture.History.Repository.Paths.GetPackPath(prepared.PackId)));
        var journal = ReadJournal(fixture, prepared);
        Assert.AreNotEqual(HistoryRestoreTransactionPhase.Complete, journal.Phase);
        CollectionAssert.AreEqual(new[] { fixture.Source }, journal.AppliedSources.ToArray());
        Assert.IsNotNull(journal.IntendedPackBytes);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        { await using var lease = await fixture.History.MutationGate.EnterAsync(); });
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() =>
            fixture.History.Commit.FindRequiredBoundaryRecapturesAsync(fixture.Snapshot, [fixture.Source]));
        await using (var independent = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(new("independent-" + Guid.NewGuid().ToString("N")))) { }

        for (var restart = 0; restart < 2; restart++)
        {
            await fixture.ReopenAsync();
            Assert.AreEqual(HistoryRuntimeHealth.Ready, fixture.History.Health);
            Assert.AreEqual(HistoryRestoreTransactionPhase.Complete, ReadJournal(fixture, prepared).Phase);
            Assert.AreEqual(failure == "before-pack" ? MergeSessionState.Ready : MergeSessionState.Committed, fixture.Session.State);
            Assert.HasCount(packCount + (failure == "before-pack" ? 0 : 1), await fixture.History.Repository.ReadAllPacksAsync());
            Assert.HasCount(versionCount + (failure == "before-pack" ? 0 : 1), await fixture.History.Query.GetAllVersionsAsync());
            Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(fixture.Target, "a.txt")));
            Assert.AreEqual(failure == "before-pack" ? "base-b" : "theirs-b", File.ReadAllText(Path.Combine(fixture.Target, "b.txt")));
            var workspace = (await fixture.History.WorkspaceStore.LoadAsync()).Value!;
            Assert.AreEqual(fixture.Before.GetSourceState(fixture.Other), workspace.GetSourceState(fixture.Other));
            var catalog = (await fixture.History.LocalReplicaCatalogStore.LoadAsync()).Value!;
            if (failure == "before-pack")
            {
                Assert.IsTrue(HistoryWorkspace.StateEquals(fixture.Before, workspace));
                CollectionAssert.AreEqual(fixture.CatalogBefore.Entries.ToArray(), catalog.Entries.ToArray());
            }
            else
            {
                Assert.AreEqual(prepared.Update.UpdateId, workspace.GetSourceState(fixture.Source).ActiveBranchUpdateId);
                Assert.HasCount(fixture.CatalogBefore.Entries.Length + 1, catalog.Entries);
                Assert.AreEqual(prepared.NewReplicas.Single(), catalog.Entries.Single(e => e.RepresentationId == prepared.NewReplicas.Single().RepresentationId));
            }
        }
        if (failure == "before-pack")
        {
            prepared = await fixture.Builder().BuildAsync(fixture.Session);
            var retryRestore = fixture.Restore();
            var retry = await new HistoryMergeApplyService(fixture.History, retryRestore, fixture.Builder(retryRestore))
                .ApplyAsync(fixture.Session, ready: prepared);
            Assert.IsTrue(retry.Succeeded, retry.Diagnostic);
        }
        var committedPacks = (await fixture.History.Repository.ReadAllPacksAsync()).Select(p => p.Pack.PackId).ToArray();
        var repeatedRestore = fixture.Restore();
        var repeated = await new HistoryMergeApplyService(fixture.History, repeatedRestore, fixture.Builder(repeatedRestore))
            .ApplyAsync(fixture.Session, ready: prepared);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, repeated.Status);
        CollectionAssert.AreEqual(committedPacks, (await fixture.History.Repository.ReadAllPacksAsync()).Select(p => p.Pack.PackId).ToArray());
        Assert.HasCount(1, (await fixture.History.Query.GetAllVersionsAsync()).Where(v => v.CreationKind == SourceVersionCreationKind.Merge));
        Assert.HasCount(1, (await fixture.History.Query.GetAllCheckpointsAsync()).Where(c => c.CreationKind == CheckpointCreationKind.Merge));
        Assert.HasCount(1, (await fixture.History.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged));
        await fixture.AssertMergedAndRestorableAsync(prepared);
    }

    [TestMethod]
    public async Task ActualMergeRecoveryHoldsOnlyItsConfigurationGate()
    {
        await using var fixture = new HistoryMergeArchiveFixture();
        await fixture.SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var failedRestore = fixture.Restore(new FaultBackend(new FileSystemHistoryRestoreMutationBackend(), "before-pack", cancellation),
            (_, _) => throw new IOException("Injected pre-Pack failure"));
        var result = await new HistoryMergeApplyService(fixture.History, failedRestore, fixture.Builder(failedRestore)).ApplyAsync(fixture.Session);
        Assert.AreEqual(HistoryRestoreStatus.MutationFailedRecoveryRequired, result.Status, result.Diagnostic);
        var barrier = new RecoveryBarrierBackend(new FileSystemHistoryRestoreMutationBackend());
        var recovering = fixture.Restore(barrier).RecoverIncompleteAsync(cancellation.Token);
        Task<NativeHistoryConfigurationOperationGate.Lease>? waiting = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiting = NativeHistoryConfigurationOperationGate.EnterHistoryAsync(fixture.Config, cancellation.Token).AsTask();
            Assert.IsFalse(waiting.IsCompleted, "Same configuration must wait for recovery.");
            await using (var independent = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(new("other-" + Guid.NewGuid().ToString("N")), cancellation.Token)) { }
            barrier.Release.TrySetResult();
            await recovering.WaitAsync(TimeSpan.FromSeconds(5));
            await using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            barrier.Release.TrySetResult();
            cancellation.Cancel();
            try { await recovering; } catch (OperationCanceledException) { }
            if (waiting is not null)
            {
                try { await (await waiting).DisposeAsync(); } catch (OperationCanceledException) { }
            }
        }
        Assert.AreEqual(fixture.OursDigest, (await MergeTreeManifest.ReadAsync(fixture.Target, _ => true, default)).Digest);
    }

    private static HistoryRestoreTransactionJournal ReadJournal(HistoryMergeArchiveFixture fixture, PreparedMerge prepared)
        => JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(Path.Combine(
            fixture.History.Repository.Paths.GetTransactionDirectory(prepared.TransactionId), "restore-journal.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private class DelegatingBackend(IHistoryRestoreMutationBackend inner) : IHistoryRestoreMutationBackend
    {
        public HistoryRestoreRollbackSnapshot PlanRollback(HistoryRestoreSourceBinding binding, HistoryTransactionId id) => inner.PlanRollback(binding, id);
        public Task PrepareRollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => inner.PrepareRollbackAsync(snapshot, token);
        public Task ApplyAsync(HistoryRestoreSourceBinding binding, string staging, HistoryRestoreApplyMode mode, HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
            => inner.ApplyAsync(binding, staging, mode, snapshot, token);
        public virtual Task RollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => inner.RollbackAsync(snapshot, token);
        public virtual Task CommitAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => inner.CommitAsync(snapshot, token);
    }

    private sealed class FaultBackend(IHistoryRestoreMutationBackend inner, string failure, CancellationTokenSource cancellation) : DelegatingBackend(inner)
    {
        public override Task RollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
            => failure == "before-pack" ? Task.FromException(new IOException("Injected deferred rollback")) : base.RollbackAsync(snapshot, token);
        public override Task CommitAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
        {
            if (failure == "cleanup-cancel")
            {
                cancellation.Cancel();
                return Task.FromException(new OperationCanceledException(cancellation.Token));
            }
            return failure == "cleanup-failure" ? Task.FromException(new IOException("Injected deferred cleanup")) : base.CommitAsync(snapshot, token);
        }
    }

    private sealed class RecoveryBarrierBackend(IHistoryRestoreMutationBackend inner) : DelegatingBackend(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task RollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); await base.RollbackAsync(snapshot, token); }
    }
}
