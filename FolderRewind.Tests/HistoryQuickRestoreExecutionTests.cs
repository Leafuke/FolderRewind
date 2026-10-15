using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryQuickRestoreExecutionTests
{
    [TestMethod]
    public async Task StagingPatchIsAppliedOnceAndCannotReportUnmodifiedExactWorkspace()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        int patches = 0;
        File.WriteAllText(Path.Combine(f.Target, "b.txt"), "preserved current");
        using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = f.Restore(verification: verification, targetVolume: true, allowSwap: false,
            prepare: (_, stage, _) => { patches++; File.WriteAllText(Path.Combine(stage, "b.txt"), "preserved current"); return Task.FromResult(true); });
        var result = await HistoryQuickRestoreExecution.ExecuteAsync(f.History, restore, new(f.Source, f.Target),
            f.Ours.NewVersions.Single().VersionId, f.Before, HistoryRestoreApplyMode.Clean, false, true,
            _ => throw new AssertFailedException("Snapshot disabled"), _ => Task.CompletedTask, default);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual(1, patches);
        Assert.AreEqual("preserved current", File.ReadAllText(Path.Combine(f.Target, "b.txt")));
        Assert.AreEqual(WorkspaceBaselineRelation.Derived, (await f.History.WorkspaceStore.LoadAsync()).Value!.GetSourceState(f.Source).Relation);
    }
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task IndependentSnapshotOptionsReuseOneMaterialization(bool ordinary, bool quick)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var settings = new ArchiveSettings { BackupBeforeRestore = ordinary, QuickRestoreSafetySnapshotEnabled = quick };
        Assert.AreEqual(ordinary, settings.ShouldCreateSafetySnapshot(false));
        foreach (var changed in new[] { false, true })
        {
            File.WriteAllText(Path.Combine(f.Target, "a.txt"), changed ? "local changed" : "ours-a");
            int snapshots = 0, preparations = 0;
            using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
            var restore = f.Restore(verification: verification, targetVolume: true,
                prepare: (_, _, _) => { preparations++; return Task.FromResult(false); });
            var diagnostic = HistoryOperationDiagnostics.Begin("quick-options");
            HistoryRestoreResult result;
            using (diagnostic)
                result = await HistoryQuickRestoreExecution.ExecuteAsync(f.History, restore, new(f.Source, f.Target),
                    f.Ours.NewVersions.Single().VersionId, (await f.History.WorkspaceStore.LoadAsync()).Value!,
                    HistoryRestoreApplyMode.Clean, settings.ShouldCreateSafetySnapshot(true), false,
                    async token => { snapshots++; await f.CaptureAsync(f.Source); return ((await f.History.WorkspaceStore.LoadAsync(token)).Value, null); },
                    _ => Task.CompletedTask, default);
            Assert.AreEqual(changed ? HistoryRestoreStatus.Committed : HistoryRestoreStatus.NoChanges, result.Status, result.Diagnostic);
            Assert.AreEqual(changed && quick ? 1 : 0, snapshots);
            Assert.AreEqual(1, preparations);
            Assert.AreEqual(1L, diagnostic.Summary!.Counters["7z-x"]);
            Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
            Assert.IsFalse(Directory.EnumerateDirectories(f.Root, ".folderrewind-restore-*").Any());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedSnapshotOrCancellationCannotMutateTarget(bool cancel)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        File.WriteAllText(Path.Combine(f.Target, "a.txt"), "local original");
        using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        using var cancellation = new CancellationTokenSource();
        var restore = f.Restore(verification: verification, targetVolume: true);
        var result = await HistoryQuickRestoreExecution.ExecuteAsync(f.History, restore, new(f.Source, f.Target),
            f.Ours.NewVersions.Single().VersionId, f.Before, HistoryRestoreApplyMode.Clean, true, false,
            _ => {
                if (cancel) cancellation.Cancel();
                return Task.FromResult<(FolderRewind.History.LocalState.HistoryWorkspace?, HistoryRestoreResult?)>(
                    (f.Before, cancel ? null : new(HistoryRestoreStatus.BlockedBeforeMutation, "snapshot failed", false, [])));
            }, _ => Task.CompletedTask, cancellation.Token);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status, result.Diagnostic);
        Assert.AreEqual("local original", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Root, ".folderrewind-restore-*").Any());
    }
}
