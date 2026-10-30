using FolderRewind.Models;
using FolderRewind.Services;
using System.Collections.Specialized;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LogPresentationControllerTests
{
    private static LogEntry Entry(int index, LogLevel level = LogLevel.Info)
        => new() { Message = index.ToString(), Level = level, Sequence = index + 1 };

    private static LogEntry Published(long sequence)
        => new() { Message = sequence.ToString(), Sequence = sequence };

    [TestMethod]
    public void DelayedSnapshotEventsStayIgnoredAfterTheirEntriesAreEvicted()
    {
        var controller = new LogPresentationController(2);
        var generation = controller.BeginSession();
        var old = Published(4);
        controller.LoadSnapshot(new[] { old, Published(5) });
        controller.Append(Published(6), generation);
        Assert.IsFalse(controller.Append(old, generation));
        CollectionAssert.AreEqual(new[] { "5", "6" }, controller.FilteredEntries.Select(e => e.Message).ToArray());
    }

    [TestMethod]
    public void EventsOlderThanSnapshotWindowCannotDisplaceFreshEntries()
    {
        var controller = new LogPresentationController(2);
        var generation = controller.BeginSession();
        controller.LoadSnapshot(new[] { Published(4), Published(5) });
        Assert.IsFalse(controller.Append(Published(1), generation));
        CollectionAssert.AreEqual(new[] { "4", "5" }, controller.FilteredEntries.Select(e => e.Message).ToArray());
    }

    [TestMethod]
    public void ConcurrentCallbacksKeepPublicationOrderWithoutResettingCollection()
    {
        var controller = new LogPresentationController(3);
        var generation = controller.BeginSession();
        var resets = 0;
        controller.FilteredEntries.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        controller.Append(Published(3), generation);
        controller.Append(Published(1), generation);
        controller.Append(Published(2), generation);
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, controller.FilteredEntries.Select(e => e.Message).ToArray());
        Assert.AreEqual(0, resets);
        controller.SetFilter(LogLevel.Info, null);
        // Test after a filter as well, so both backing and visible orders are checked.
        Assert.HasCount(0, controller.FilteredEntries);
        controller.SetFilter(null, null);
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, controller.FilteredEntries.Select(e => e.Message).ToArray());
        Assert.AreEqual(2, resets);
        controller.Append(Published(4), generation);
        Assert.IsFalse(controller.Append(Published(1), generation));
        CollectionAssert.AreEqual(new[] { "2", "3", "4" }, controller.FilteredEntries.Select(e => e.Message).ToArray());
    }

    [TestMethod]
    public void LiveOverflowKeepsLatest5000WithoutDuplicatesOrResets()
    {
        var controller = new LogPresentationController();
        var generation = controller.BeginSession();
        var resetCount = 0;
        var notificationCount = 0;
        controller.FilteredEntries.CollectionChanged += (_, args) =>
        {
            notificationCount++;
            if (args.Action == NotifyCollectionChangedAction.Reset) resetCount++;
        };
        for (var i = 0; i < 6000; i++) controller.Append(Entry(i), generation);
        Assert.HasCount(5000, controller.FilteredEntries);
        CollectionAssert.AreEqual(Enumerable.Range(1000, 5000).Select(i => i.ToString()).ToArray(), controller.FilteredEntries.Select(e => e.Message).ToArray());
        Assert.AreEqual(0, resetCount);
        Assert.AreEqual(7000, notificationCount);
    }

    [TestMethod]
    public void SnapshotOverlapIsDeduplicatedAndStaleCallbacksAreIgnored()
    {
        var controller = new LogPresentationController();
        var first = controller.BeginSession();
        var entry = Entry(1);
        controller.LoadSnapshot(new[] { entry });
        Assert.IsFalse(controller.Append(entry, first));
        controller.EndSession();
        Assert.IsFalse(controller.Append(Entry(2), first));
        var second = controller.BeginSession();
        Assert.IsFalse(controller.Append(Entry(3), first));
        Assert.IsTrue(controller.Append(Entry(4), second));
        Assert.HasCount(2, controller.FilteredEntries);
    }

    [TestMethod]
    public void PauseKeepsVisibleEntriesAndResumePublishesLatestBufferOnce()
    {
        var controller = new LogPresentationController(2);
        var generation = controller.BeginSession();
        controller.Append(Entry(1), generation);
        var bound = controller.FilteredEntries;
        controller.SetLive(false);
        controller.Append(Entry(2), generation);
        controller.Append(Entry(3), generation);
        CollectionAssert.AreEqual(new[] { "1" }, bound.Select(e => e.Message).ToArray());
        var notifications = 0;
        bound.CollectionChanged += (_, _) => notifications++;
        controller.SetLive(true);
        Assert.AreSame(bound, controller.FilteredEntries);
        CollectionAssert.AreEqual(new[] { "2", "3" }, bound.Select(e => e.Message).ToArray());
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public void FilteringAndSnapshotRespectCapacityAndMatchAllTextFields()
    {
        var controller = new LogPresentationController(2);
        var generation = controller.BeginSession();
        controller.LoadSnapshot(new[] { Entry(0), new LogEntry { Level = LogLevel.Error, Source = "MATCH" }, Entry(2) });
        controller.SetFilter(LogLevel.Error, "match");
        Assert.HasCount(1, controller.FilteredEntries);
        controller.Append(new LogEntry { Level = LogLevel.Error, Exception = "match" }, generation);
        controller.Append(Entry(4), generation);
        Assert.HasCount(1, controller.FilteredEntries);
        Assert.AreEqual("match", controller.FilteredEntries[0].Exception);
    }
}
