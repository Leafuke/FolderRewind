using FolderRewind.Services;
using System.Collections.Specialized;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRefreshCoordinationTests
{
    [TestMethod]
    public void ReplaceAll_RaisesOneResetForLargeResult()
    {
        var collection = new BatchObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);

        collection.ReplaceAll(Enumerable.Range(0, 1_000));

        Assert.HasCount(1_000, collection);
        Assert.HasCount(1, notifications);
        Assert.AreEqual(NotifyCollectionChangedAction.Reset, notifications[0].Action);
    }

    [TestMethod]
    public void Begin_CancelsPreviousRequestAndOnlyLatestCanPublish()
    {
        using var coordinator = new LatestRequestCoordinator();
        using var first = coordinator.Begin();
        using var second = coordinator.Begin();

        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.IsFalse(first.IsCurrent);
        Assert.IsFalse(second.Token.IsCancellationRequested);
        Assert.IsTrue(second.IsCurrent);
    }

    [TestMethod]
    public void CancelCurrent_InvalidatesPendingRequest()
    {
        using var coordinator = new LatestRequestCoordinator();
        using var request = coordinator.Begin();

        coordinator.CancelCurrent();

        Assert.IsTrue(request.Token.IsCancellationRequested);
        Assert.IsFalse(request.IsCurrent);
    }

    [TestMethod]
    public void ExternalCancellation_InvalidatesPendingRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var coordinator = new LatestRequestCoordinator();
        using var request = coordinator.Begin(cancellation.Token);

        cancellation.Cancel();

        Assert.IsTrue(request.Token.IsCancellationRequested);
        Assert.IsFalse(request.IsCurrent);
    }

    [TestMethod]
    public void Begin_FaultyCancellationCallbackDoesNotBlockNewRequest()
    {
        using var coordinator = new LatestRequestCoordinator();
        using var first = coordinator.Begin();
        using var registration = first.Token.Register(() => throw new InvalidOperationException("callback failed"));

        using var second = coordinator.Begin();

        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.IsTrue(second.IsCurrent);
    }
}
