using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class AsyncOperationQueueTests
{
    [TestMethod]
    public async Task ConcurrentOperationsRunOneAtATimeInRequestOrder()
    {
        var queue = new AsyncOperationQueue();
        var order = new List<int>();
        var activeCount = 0;
        var maximumActiveCount = 0;

        var tasks = Enumerable.Range(0, 8)
            .Select(index => queue.EnqueueAsync(async cancellationToken =>
            {
                var currentActiveCount = Interlocked.Increment(ref activeCount);
                maximumActiveCount = Math.Max(maximumActiveCount, currentActiveCount);
                order.Add(index);
                await Task.Delay(5, cancellationToken);
                Interlocked.Decrement(ref activeCount);
                return index;
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        CollectionAssert.AreEqual(Enumerable.Range(0, 8).ToArray(), results);
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).ToArray(), order);
        Assert.AreEqual(1, maximumActiveCount);
    }

    [TestMethod]
    public async Task QueuedCancellationDoesNotRunCanceledOperation()
    {
        var queue = new AsyncOperationQueue();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.EnqueueAsync(async _ =>
        {
            await releaseFirst.Task;
            return 1;
        });
        using var cancellation = new CancellationTokenSource();
        var ranCanceledOperation = false;
        var canceled = queue.EnqueueAsync(_ =>
        {
            ranCanceledOperation = true;
            return Task.FromResult(2);
        }, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await canceled);
        releaseFirst.SetResult();

        Assert.AreEqual(1, await first);
        Assert.IsFalse(ranCanceledOperation);
    }

    [TestMethod]
    public async Task OperationFailureReleasesQueueForNextRequest()
    {
        var queue = new AsyncOperationQueue();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await queue.EnqueueAsync<int>(_ => throw new InvalidOperationException("test")));

        Assert.AreEqual(42, await queue.EnqueueAsync(_ => Task.FromResult(42)));
    }

    [TestMethod]
    public async Task RunningCancellationDoesNotAllowTheNextOperationToOverlap()
    {
        var queue = new AsyncOperationQueue();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var first = queue.EnqueueAsync(async _ =>
        {
            operationStarted.SetResult();
            await releaseOperation.Task;
            return 1;
        }, cancellation.Token);

        await operationStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);

        var secondStarted = false;
        var second = queue.EnqueueAsync(_ =>
        {
            secondStarted = true;
            return Task.FromResult(2);
        });
        await Task.Delay(20);
        Assert.IsFalse(secondStarted);

        releaseOperation.SetResult();
        Assert.AreEqual(2, await second);
    }
}
