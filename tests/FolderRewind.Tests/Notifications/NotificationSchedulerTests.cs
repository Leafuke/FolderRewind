using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NotificationSchedulerTests
{
    private static NotificationBannerRequest Banner(NotificationEvent item, bool quiet = false) => new(item, 0, quiet);

    [TestMethod]
    public void ConsecutiveNotificationsEachReceiveTheirOwnTimeout()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "first", duration: 1000)));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "second", duration: 1000)));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "third", duration: 1000)));
        clock.Advance(1000); scheduler.Advance(); Assert.AreEqual("second", scheduler.Current!.Event.Id);
        clock.Advance(1000); scheduler.Advance(); Assert.AreEqual("third", scheduler.Current!.Event.Id);
        clock.Advance(1000); scheduler.Advance(); Assert.IsNull(scheduler.Current);
    }

    [TestMethod]
    public void PersistentAnnouncementYieldsToBusinessResultAndReturns()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "announcement", category: NotificationCategory.Announcement, duration: 0)));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "result", duration: 1000)));
        Assert.AreEqual("result", scheduler.Current!.Event.Id);
        clock.Advance(1000); scheduler.Advance();
        Assert.AreEqual("announcement", scheduler.Current!.Event.Id); Assert.IsTrue(scheduler.Current.Resume);
        clock.Advance(100000); scheduler.Advance(); Assert.AreEqual("announcement", scheduler.Current!.Event.Id);
    }

    [TestMethod]
    public void PreemptionPreservesRemainingDuration()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "success", duration: 5000)));
        clock.Advance(2000);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "error")));
        clock.Advance(5000); scheduler.Advance();
        Assert.AreEqual("success", scheduler.Current!.Event.Id); Assert.AreEqual(3000d, scheduler.RemainingMilliseconds);
        clock.Advance(2999); scheduler.Advance(); Assert.IsNotNull(scheduler.Current);
        clock.Advance(1); scheduler.Advance(); Assert.IsNull(scheduler.Current);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void HoverAndKeyboardProtectTimeoutAndPreemption(bool hover, bool focus)
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "reading", duration: 5000)));
        clock.Advance(1000); scheduler.SetInteraction(hover, focus);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "urgent")));
        clock.Advance(60000); scheduler.Advance();
        Assert.AreEqual("reading", scheduler.Current!.Event.Id); Assert.AreEqual(4000d, scheduler.RemainingMilliseconds);
        scheduler.SetInteraction(false, false); Assert.AreEqual("urgent", scheduler.Current!.Event.Id);
        scheduler.Close(); Assert.AreEqual("reading", scheduler.Current!.Event.Id); Assert.AreEqual(4000d, scheduler.RemainingMilliseconds);
    }

    [TestMethod]
    public void ProtectedReplacementWaitsAndOnlyNewestResultIsPresented()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "job") with { Message = "initial" }));
        scheduler.SetInteraction(false, true);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "job") with { Message = "intermediate" }, true));
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "job") with { Message = "final" }, true));
        Assert.AreEqual("initial", scheduler.Current!.Event.Message); Assert.AreEqual(1, scheduler.PendingCount);
        scheduler.SetInteraction(false, false); Assert.AreEqual("final", scheduler.Current!.Event.Message);
    }

    [TestMethod]
    public void ProtectedSuccessReplacementSurvivesExpirationAndQueuePressure()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock, 2);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "job")));
        scheduler.SetInteraction(false, true);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "job", message: "recovered"), true));
        for (var i = 0; i < 5; i++) scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "other-" + i)));
        clock.Advance(60000); scheduler.Advance();
        scheduler.SetInteraction(false, false);
        Assert.AreEqual("other-4", scheduler.Current!.Event.Id);
        scheduler.Close(); Assert.AreEqual("recovered", scheduler.Current!.Event.Message);
    }

    [TestMethod]
    public void ExplicitCloseWhileProtectedDoesNotDismissTheWaitingError()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "current")));
        scheduler.SetInteraction(true, true);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "waiting")));
        scheduler.Close(); Assert.AreEqual("waiting", scheduler.Current!.Event.Id);
    }

    [TestMethod]
    public void QueueBoundsAndFifoFavorMoreImportantMessages()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "current")));
        for (var i = 0; i < 40; i++)
        {
            scheduler.Enqueue(Banner(NotificationTestData.Item(clock, i.ToString(), NotificationSeverity.Warning, NotificationImportance.Important)));
            clock.Advance(1);
        }
        Assert.AreEqual(32, scheduler.PendingCount);
        scheduler.Close(); Assert.AreEqual("8", scheduler.Current!.Event.Id);
        scheduler.Close(); Assert.AreEqual("9", scheduler.Current!.Event.Id);
    }

    [TestMethod]
    public void OrdinaryQueuedMessagesExpireButWarningsWaitForInteraction()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "current")));
        scheduler.SetInteraction(true, false);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "success")));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "warning", NotificationSeverity.Warning, NotificationImportance.Important)));
        clock.Advance(30001); scheduler.Advance();
        Assert.AreEqual(1, scheduler.PendingCount);
        scheduler.Close(); Assert.AreEqual("warning", scheduler.Current!.Event.Id);
    }

    [TestMethod]
    public void BackgroundDropsTransientQueueWithoutReplayAndKeepsPersistentState()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "announcement", category: NotificationCategory.Announcement, duration: 0)));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "transient")));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "queued")));
        scheduler.SetForeground(false);
        scheduler.Enqueue(Banner(NotificationTestData.Failure(clock, "while-background")));
        scheduler.SetForeground(true);
        Assert.AreEqual("announcement", scheduler.Current!.Event.Id); Assert.AreEqual(0, scheduler.PendingCount);
    }

    [TestMethod]
    public async Task ResumingPreviouslySubmittedBannerDoesNotConsumeAnotherAlert()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        var environment = NotificationTestData.Background with { Foreground = true };
        var core = new NotificationCoordinator(clock, () => environment, new NotificationFakeSystem(), new NotificationFakeSound());
        core.BannerRequested += request => scheduler.Enqueue(request);
        await core.PublishAsync(NotificationTestData.Item(clock, "batch", once: true));
        core.ReportBannerShown(scheduler.Current!);
        await core.PublishAsync(NotificationTestData.Failure(clock, "error"));
        core.ReportBannerShown(scheduler.Current!);
        scheduler.Close();
        Assert.AreEqual("batch", scheduler.Current!.Event.Id); Assert.IsTrue(core.CanShowBanner(scheduler.Current));
        var old = scheduler.Current;
        environment = environment with { Generation = 1 };
        Assert.IsFalse(core.CanShowBanner(old));
    }

    [TestMethod]
    public async Task DelayedUiDispatchCannotReplaceANewerOperationResult()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        var requests = new List<NotificationBannerRequest>();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background with { Foreground = true },
            new NotificationFakeSystem(), new NotificationFakeSound());
        core.BannerRequested += requests.Add;
        await core.PublishAsync(NotificationTestData.Item(clock, "batch", message: "first"));
        await core.PublishAsync(NotificationTestData.Item(clock, "batch", message: "final"));
        foreach (var request in requests.AsEnumerable().Reverse())
            if (core.CanQueueBanner(request)) scheduler.Enqueue(request);
        Assert.AreEqual("final", scheduler.Current!.Event.Message);
        Assert.IsFalse(core.CanQueueBanner(requests[0]));
    }

    [TestMethod]
    public void QuietUpdatesDoNotCreateNewBanners()
    {
        var clock = new NotificationTestClock(); var scheduler = new NotificationScheduler(clock);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "quiet"), true));
        Assert.IsNull(scheduler.Current);
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "visible")));
        scheduler.Enqueue(Banner(NotificationTestData.Item(clock, "visible", message: "updated"), true));
        Assert.AreEqual("updated", scheduler.Current!.Event.Message);
    }

    [TestMethod]
    public void ActivationWaitsForHostAndFreezesArguments()
    {
        var pending = new NotificationActivationQueue();
        var args = new Dictionary<string, string> { ["target"] = "Tasks" };
        pending.Enqueue(args); args["target"] = "History";
        Assert.IsFalse(pending.TryTake(out _));
        pending.SetReady(true); Assert.IsTrue(pending.TryTake(out var captured)); Assert.AreEqual("Tasks", captured["target"]);
        Assert.IsFalse(pending.TryTake(out _));
    }

    [TestMethod]
    public void ActivationHandlesCanceledNavigationMissingRecordsAndLegacyTargets()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock);
        store.Upsert(NotificationTestData.Item(clock));
        var opened = 0; var details = 0; var unavailable = 0;
        Assert.IsFalse(NotificationActivationRouter.Route(new Dictionary<string, string> { ["notification"] = "one" },
            store.Find, id => store.MarkRead(id), _ => { opened++; return false; }, _ => throw new Exception(), _ => details++, () => unavailable++));
        Assert.AreEqual(1, opened); Assert.AreEqual(1, details); Assert.AreEqual(0, store.UnreadCount);
        Assert.IsTrue(NotificationActivationRouter.Route(new Dictionary<string, string> { ["notification"] = "previous-session", ["kind"] = "History", ["config"] = "config-a" },
            store.Find, _ => Assert.Fail(), target => target.ConfigId == "config-a", _ => throw new Exception(), _ => Assert.Fail(), () => unavailable++));
        Assert.IsTrue(NotificationActivationRouter.Route(new Dictionary<string, string> { ["target"] = "Tasks" },
            store.Find, _ => Assert.Fail(), _ => throw new Exception(), page => page == "Tasks", _ => Assert.Fail(), () => unavailable++));
        Assert.IsFalse(NotificationActivationRouter.Route(new Dictionary<string, string> { ["target"] = "untrusted", ["kind"] = "99" },
            store.Find, _ => Assert.Fail(), _ => throw new Exception(), _ => throw new Exception(), _ => Assert.Fail(), () => unavailable++));
        Assert.AreEqual(1, unavailable);
    }

    [TestMethod]
    public void LegacyToastTargetWorksWithAndWithoutItsSessionRecord()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock, 1);
        var item = NotificationEvent.Capture(new() { Title = "legacy", Category = NotificationCategory.BusinessResult },
            NotificationImportance.Info, clock, text => text, recordIdOverride: "legacy-tag");
        store.Upsert(item);
        var args = new Dictionary<string, string> { ["notification"] = item.Id, ["kind"] = "None", ["target"] = "Home" };
        bool Route() => NotificationActivationRouter.Route(args, store.Find, id => store.MarkRead(id), _ => throw new Exception(),
            page => page == "Home", _ => Assert.Fail(), () => Assert.Fail());
        Assert.IsTrue(Route()); Assert.AreEqual(0, store.UnreadCount);
        clock.Advance(1); store.Upsert(NotificationTestData.Item(clock, "new"));
        Assert.IsNull(store.Find(item.Id)); Assert.IsTrue(Route());
    }
}
