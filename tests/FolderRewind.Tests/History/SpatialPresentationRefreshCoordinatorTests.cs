using FolderRewind.Services;
using System.Collections.Concurrent;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialPresentationRefreshCoordinatorTests
{
    [TestMethod]
    public async Task ScanBurstKeepsOneRunningReadAndOnlyTheLatestPendingRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<int>();
        int active = 0, peak = 0;
        await using var coordinator = new SpatialPresentationRefreshCoordinator<int>(async (value, token) =>
        {
            int count = Interlocked.Increment(ref active); peak = Math.Max(peak, count);
            try
            {
                seen.Enqueue(value);
                if (value == 0) { entered.SetResult(); await release.Task.WaitAsync(token); }
            }
            finally { Interlocked.Decrement(ref active); }
        }, TimeSpan.Zero);
        var first = coordinator.RequestAsync(0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = Enumerable.Range(1, 1000).Select(i => coordinator.RequestAsync(i, immediate: false)).ToArray();
        Assert.HasCount(1, seen);
        Assert.IsFalse(first.IsCompleted, "Scan updates must not cancel the useful current read.");
        Assert.IsTrue(pending.Take(999).All(t => t.IsCompleted), "Superseded requests must not accumulate waiters.");
        release.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.DrainAsync();
        CollectionAssert.AreEqual(new[] { 0, 1000 }, seen.ToArray());
        Assert.AreEqual(1, peak);
    }

    [TestMethod]
    public async Task UserRequestCancelsTheOldReadAndPublishesOnlyItsReplacement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new ConcurrentQueue<int>();
        await using var coordinator = new SpatialPresentationRefreshCoordinator<int>(async (value, token) =>
        {
            if (value == 0)
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.SetResult(); throw; }
            }
            token.ThrowIfCancellationRequested(); published.Enqueue(value);
        });
        var old = coordinator.RequestAsync(0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var latest = coordinator.RequestAsync(1);
        await Task.WhenAll(old, latest, canceled.Task).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 1 }, published.ToArray());
    }

    [TestMethod]
    public async Task DisposeDropsPendingWorkAndWaitsForTheRunningReadBeforeReleasingIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<int>();
        var coordinator = new SpatialPresentationRefreshCoordinator<int>(async (value, _) =>
        { seen.Enqueue(value); entered.SetResult(); await release.Task; }, TimeSpan.Zero);
        var running = coordinator.RequestAsync(0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = coordinator.RequestAsync(1, immediate: false);
        var disposing = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposing.IsCompleted);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(running, disposing).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.DisposeAsync();
        CollectionAssert.AreEqual(new[] { 0 }, seen.ToArray());
        Assert.ThrowsExactly<ObjectDisposedException>(() => coordinator.RequestAsync(2));
    }

    [TestMethod]
    public async Task ScanThrottleUsesTheFirstDeadlineAndAnImmediateRequestBypassesIt()
    {
        var clock = new NotificationTestClock();
        var seen = new ConcurrentQueue<int>();
        await using var coordinator = new SpatialPresentationRefreshCoordinator<int>((value, _) =>
        { seen.Enqueue(value); return Task.CompletedTask; }, clock: clock);
        await coordinator.RequestAsync(1);
        await coordinator.DrainAsync();
        clock.Advance(249);
        var pending = coordinator.RequestAsync(2, immediate: false);
        await Task.Delay(20);
        CollectionAssert.AreEqual(new[] { 1 }, seen.ToArray());
        clock.Advance(1);
        await coordinator.RequestAsync(3, immediate: false).WaitAsync(TimeSpan.FromSeconds(5));
        await pending;
        await coordinator.DrainAsync();
        var delayed = coordinator.RequestAsync(4, immediate: false);
        await coordinator.RequestAsync(5).WaitAsync(TimeSpan.FromSeconds(5));
        await delayed;
        await coordinator.DrainAsync();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, seen.ToArray());
    }

    [TestMethod]
    public async Task ClosingAfterReadBeforeUiPublicationRejectsTheLateResult()
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;
        var coordinator = new SpatialPresentationRefreshCoordinator<int>(async (_, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            read.SetResult(); await ui.Task;
            token.ThrowIfCancellationRequested(); published = true;
        });
        var request = coordinator.RequestAsync(0);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = coordinator.DisposeAsync().AsTask();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ui.SetResult();
        await Task.WhenAll(request, closing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(published);
    }

    [TestMethod]
    public async Task FailedReadDoesNotPoisonLaterRequests()
    {
        int published = -1;
        await using var coordinator = new SpatialPresentationRefreshCoordinator<int>((value, _) =>
        {
            if (value == 0) throw new InvalidDataException("fixture read failed");
            published = value; return Task.CompletedTask;
        }, TimeSpan.Zero);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.RequestAsync(0));
        await coordinator.RequestAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, published);
    }
}
