using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRestoreCapacityTests
{
    [TestMethod]
    public async Task MultiSourceBudgetRejectsCumulativeDemandBeforeExtractingOrChangingTargets()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        long available = 12L * 1024 * 1024; // 每份不足 9 MiB，两份同卷累计超过预算。
        var budget = new ArtifactSpaceBudget(_ => new("physical-test-volume", available));
        var original = WindowsRestoreFileSystem.Identity(f.Target);
        var diagnostics = HistoryOperationDiagnostics.Begin("cumulative-space");
        using (diagnostics)
        {
            var result = await f.Restore(targetVolume: true, spaceBudget: budget).RestoreVersionsAsync(
                new Dictionary<SourceId, VersionId> { [f.Source] = f.Ours.NewVersions.Single().VersionId,
                    [f.Other] = f.Before.GetSourceState(f.Other).BaseVersionId!.Value },
                [new(f.Source, f.Target), new(f.Other, f.OtherTarget)], f.Before, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status, result.Diagnostic);
            StringAssert.Contains(result.Diagnostic, "space");
        }
        Assert.AreEqual(0L, diagnostics.Summary!.Counters.GetValueOrDefault("7z-x"));
        Assert.AreEqual(original, WindowsRestoreFileSystem.Identity(f.Target));
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
        await f.History.DrainRestoreCleanupAsync();
        HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot);
        // 失败路径必须退还先前预留，后续操作可正常准备。
        var retry = await f.Restore(targetVolume: true, spaceBudget: budget).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
            new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(retry.Succeeded, retry.Diagnostic);
    }

    [TestMethod]
    public async Task CompletedMaterializationReleasesReservationBeforeSafetyCapture()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var budget = new ArtifactSpaceBudget(_ => new("one-volume", 12L * 1024 * 1024));
        var restore = f.Restore(targetVolume: true, spaceBudget: budget);
        using var scope = restore.EnterInputScope();
        using var session = restore.CreatePreparationSession([new(f.Source, f.Target)], f.Before);
        var version = f.Ours.NewVersions.Single();
        await restore.ReservePreparationSpaceAsync([(version, new(f.Source, f.Target), MaterializationFidelity.Exact)], session, default);
        await restore.PrepareSourceAsync(version, new(f.Source, f.Target), MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, default, session);
        using var snapshot = budget.Reserve(new ArtifactSpaceRequest(f.Target, 10L * 1024 * 1024));
    }
}
