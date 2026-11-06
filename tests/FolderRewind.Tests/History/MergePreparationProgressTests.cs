using FolderRewind.History.Merge;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MergePreparationProgressTests
{
    [TestMethod]
    public void CountsAreLimitedToTenPerSecondAndStageChangesAreImmediate()
    {
        var clock = new Clock(); var received = new List<MergePreparationProgress>();
        var reporter = new MergePreparationReporter(received.Add, clock);
        reporter.Report(MergePreparationStage.PreparingHistory);
        for (var i = 0; i < 1000; i++) reporter.Report(MergePreparationStage.PreparingHistory, i);
        Assert.HasCount(1, received);
        clock.Advance(99); reporter.Report(MergePreparationStage.PreparingHistory, 1); Assert.HasCount(1, received);
        clock.Advance(1); reporter.Report(MergePreparationStage.PreparingHistory, 2); Assert.HasCount(2, received);
        reporter.Report(MergePreparationStage.AnalyzingWorld); Assert.HasCount(3, received);
        new MergePreparationReporter(_ => throw new InvalidOperationException()).Report(MergePreparationStage.OrganizingResults);
    }

    [TestMethod]
    public async Task ProgressDoesNotPublishASessionAndOldGenerationCannotOverwriteTheNewOperation()
    {
        var tracker = new MergeOperationTracker(); Action<MergePreparationProgress>? stale = null;
        await tracker.RunAsync(MergeOperationStage.Analyzing, _ =>
        {
            stale = tracker.CreateProgressObserver(); stale(new(MergePreparationStage.AnalyzingWorld, 5));
            Assert.IsNull(tracker.Snapshot.Session);
            Assert.AreEqual(5, tracker.Snapshot.Progress!.Completed);
            return Task.CompletedTask;
        });
        await tracker.RunAsync(MergeOperationStage.Analyzing, _ =>
        {
            tracker.CreateProgressObserver()(new(MergePreparationStage.PreparingHistory, 1));
            stale!(new(MergePreparationStage.OrganizingResults, 999));
            Assert.AreEqual(MergePreparationStage.PreparingHistory, tracker.Snapshot.Progress!.Stage);
            Assert.AreEqual(1, tracker.Snapshot.Progress.Completed);
            return Task.CompletedTask;
        });
        Assert.IsNull(tracker.Snapshot.Progress);
    }

    [TestMethod]
    public async Task ReplacingAnIndexCancelsAndDrainsOldReadLeasesBeforeDisposal()
    {
        await using var lifetime = new SpatialIndexReadLifetime();
        var lease = lifetime.Enter(default);
        var drained = lifetime.StopAsync();
        Assert.IsTrue(lease.Token.IsCancellationRequested); Assert.IsFalse(drained.IsCompleted);
        Assert.ThrowsExactly<OperationCanceledException>(() => lifetime.Enter(default));
        lease.Dispose(); await drained.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        internal void Advance(long milliseconds) => _milliseconds += milliseconds;
    }
}
