using FolderRewind.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ConfigWriteCoordinatorTests
{
    [TestMethod]
    public async Task SnapshotRequestsCoalesceBeforeCaptureAndWaitForDurability()
    {
        var posted = new List<Action>();
        var values = new List<int>();
        var captures = 0;
        int[]? snapshot = null;
        var disk = new TaskCompletionSource<FolderRewind.Models.ConfigSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ConfigSnapshotCoordinator<int[]>(posted.Add,
            () => { captures++; return values.ToArray(); },
            (copy, _) => { snapshot = copy; return disk.Task; });
        var requests = Enumerable.Range(0, 50).Select(i => coordinator.EnqueueAsync(() => values.Add(i), true, default)).ToArray();
        Assert.HasCount(1, posted);
        posted[0]();
        Assert.AreEqual(1, captures);
        CollectionAssert.AreEqual(Enumerable.Range(0, 50).ToArray(), snapshot);
        values.Add(999);
        Assert.HasCount(50, snapshot!);
        Assert.IsTrue(requests.All(task => !task.IsCompleted));
        disk.SetResult(new() { Success = true });
        Assert.IsTrue((await Task.WhenAll(requests)).All(result => result.Success));
        coordinator.Seal();
        Assert.IsFalse((await coordinator.EnqueueAsync(null, false, default)).Success);
    }

    [TestMethod]
    public async Task SnapshotCancellationAndFailedMutationDoNotDropFollowingMutation()
    {
        Action? posted = null;
        var state = 0;
        var coordinator = new ConfigSnapshotCoordinator<int>(action => posted = action,
            () => state, (_, _) => Task.FromResult(new FolderRewind.Models.ConfigSaveResult { Success = true }));
        using var cancellation = new CancellationTokenSource();
        var canceled = coordinator.EnqueueAsync(() => state = 100, false, cancellation.Token);
        cancellation.Cancel();
        var failed = coordinator.EnqueueAsync(() => throw new InvalidOperationException("mutation failed"), false, default);
        var succeeded = coordinator.EnqueueAsync(() => state++, false, default);
        posted!();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await canceled);
        Assert.IsFalse((await failed).Success);
        Assert.IsTrue((await succeeded).Success);
        Assert.AreEqual(1, state);
    }

    [TestMethod]
    public async Task ConcurrentRequestsPersistLatestRevisionWithoutOverlappingWrites()
    {
        var writes = new ConcurrentQueue<string>();
        var activeWriters = 0;
        var maximumActiveWriters = 0;
        var savedEvents = 0;

        await using var coordinator = new ConfigWriteCoordinator(
            async (payload, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeWriters);
                maximumActiveWriters = Math.Max(maximumActiveWriters, active);
                try
                {
                    await Task.Delay(5, cancellationToken);
                    writes.Enqueue(Encoding.UTF8.GetString(payload.Span));
                }
                finally
                {
                    Interlocked.Decrement(ref activeWriters);
                }
            },
            () => Interlocked.Increment(ref savedEvents));

        var requests = Enumerable.Range(1, 50)
            .Select(revision => coordinator.EnqueueAsync(
                Encoding.UTF8.GetBytes(revision.ToString()),
                publishSavedEvent: true))
            .ToArray();

        var results = await Task.WhenAll(requests);
        await coordinator.FlushAsync();

        Assert.IsTrue(results.All(result => result.Success));
        Assert.AreEqual(1, maximumActiveWriters);
        Assert.AreEqual("50", writes.Last());
        Assert.AreEqual(50, coordinator.PersistedRevision);
        Assert.IsGreaterThanOrEqualTo(1, savedEvents);
        Assert.IsLessThanOrEqualTo(50, writes.Count);
    }

    [TestMethod]
    public async Task FailedWriteCompletesEveryCoalescedRequestWithFailure()
    {
        var expected = new IOException("disk unavailable");
        await using var coordinator = new ConfigWriteCoordinator(
            (_, _) => Task.FromException(expected));

        var requests = Enumerable.Range(0, 8)
            .Select(index => coordinator.EnqueueAsync([(byte)index], publishSavedEvent: false))
            .ToArray();

        var results = await Task.WhenAll(requests);

        Assert.IsTrue(results.All(result => !result.Success));
        Assert.IsTrue(results.All(result => ReferenceEquals(expected, result.Exception)));
        Assert.AreEqual(0, coordinator.PersistedRevision);
    }

    [TestMethod]
    public async Task CanceledQueuedRequestDoesNotPreventFollowingWrite()
    {
        var persisted = new ConcurrentQueue<byte>();
        await using var coordinator = new ConfigWriteCoordinator(
            (payload, _) =>
            {
                persisted.Enqueue(payload.Span[0]);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await coordinator.EnqueueAsync([1], publishSavedEvent: false, cancellation.Token));
        var result = await coordinator.EnqueueAsync([2], publishSavedEvent: false);
        await coordinator.FlushAsync();

        Assert.IsTrue(result.Success);
        Assert.AreEqual((byte)2, persisted.Last());
    }
}
