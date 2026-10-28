using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NotificationUxTests
{
    private static NotificationEvent Result(TimeProvider clock, string id = "one", NotificationTrigger trigger = NotificationTrigger.Automatic,
        NotificationResultKind result = NotificationResultKind.Success, NotificationOperationKind operation = NotificationOperationKind.Backup)
        => NotificationEvent.Capture(new()
        {
            RecordId = id, Category = NotificationCategory.BusinessResult,
            Content = new(operation, trigger, result, "project", "folder", new(1, 0, 0, 0)),
            Target = new(NotificationTargetKind.Tasks, id, "config"), Title = "result", Message = "summary",
            Severity = result == NotificationResultKind.Failed ? NotificationSeverity.Error : result == NotificationResultKind.Warning
                ? NotificationSeverity.Warning : NotificationSeverity.Success,
            CompletionSoundSuccess = result is NotificationResultKind.Success or NotificationResultKind.NoChanges, KeepInCenter = true
        }, null, clock, value => value);

    [TestMethod]
    [DataRow(NotificationOperationKind.Backup, NotificationResultKind.Success, false)]
    [DataRow(NotificationOperationKind.Backup, NotificationResultKind.NoChanges, true)]
    [DataRow(NotificationOperationKind.Cloud, NotificationResultKind.Success, false)]
    [DataRow(NotificationOperationKind.Cloud, NotificationResultKind.NoChanges, true)]
    public async Task AutomaticNormalResultsAreRecordedWithoutAnyAlert(NotificationOperationKind operation, NotificationResultKind result, bool foreground)
    {
        var clock = new NotificationTestClock(); var sender = new NotificationFakeSystem(); var sound = new NotificationFakeSound();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background with { Foreground = foreground }, sender, sound);
        var banners = 0; core.BannerRequested += _ => banners++;
        await core.PublishAsync(Result(clock, result: result, operation: operation));
        Assert.AreEqual(1, core.Store.UnreadCount); Assert.AreEqual(0, core.Store.ImportantUnreadCount);
        Assert.AreEqual(0, sender.Calls); Assert.AreEqual(0, sound.Submissions); Assert.AreEqual(0, banners);
    }

    [TestMethod]
    [DataRow(NotificationTrigger.Automatic, NotificationResultKind.Warning)]
    [DataRow(NotificationTrigger.Automatic, NotificationResultKind.Failed)]
    [DataRow(NotificationTrigger.Manual, NotificationResultKind.Success)]
    [DataRow(NotificationTrigger.Unknown, NotificationResultKind.Success)]
    public async Task AutomaticIssuesAndExplicitOrUnknownNormalResultsStillNotify(NotificationTrigger trigger, NotificationResultKind result)
    {
        var clock = new NotificationTestClock(); var sender = new NotificationFakeSystem();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background, sender, new NotificationFakeSound());
        await core.PublishAsync(Result(clock, trigger: trigger, result: result));
        Assert.HasCount(1, sender.Submissions);
        Assert.AreEqual(result is NotificationResultKind.Warning or NotificationResultKind.Failed ? 1 : 0, core.Store.ImportantUnreadCount);
    }

    [TestMethod]
    public void ManualRetryOverridesAutomaticOriginForOnlyThatAttemptAndJob()
    {
        var origins = new NotificationExecutionOrigins(new NotificationTestClock());
        origins.RequestManual("job-a");
        Assert.AreEqual(NotificationTrigger.Automatic, origins.Take("job-b", true));
        Assert.AreEqual(NotificationTrigger.Manual, origins.Take("job-a", true));
        Assert.AreEqual(NotificationTrigger.Automatic, origins.Take("job-a", true));
        Assert.AreEqual(NotificationTrigger.Manual, origins.Take("created-manually", false));
    }

    [TestMethod]
    public async Task OpenCenterSuppressesRecordedFeedbackButKeepsOrdinaryFeedbackAndPersistentStatus()
    {
        var clock = new NotificationTestClock(); var sound = new NotificationFakeSound();
        var environment = NotificationTestData.Background with { Foreground = true, CenterOpen = true, PresentationGeneration = 1 };
        var core = new NotificationCoordinator(clock, () => environment, new NotificationFakeSystem(), sound);
        var banners = new List<NotificationBannerRequest>(); core.BannerRequested += banners.Add;
        var item = Result(clock, trigger: NotificationTrigger.Manual);
        await core.PublishAsync(item); Assert.HasCount(0, banners); Assert.AreEqual(0, sound.Submissions);
        environment = environment with { CenterOpen = false };
        await core.PublishAsync(item); Assert.HasCount(0, banners, "Closing must not replay unchanged results.");
        environment = environment with { CenterOpen = true };
        await core.PublishAsync(NotificationTestData.Item(clock, "feedback", category: NotificationCategory.Transient));
        await core.PublishAsync(NotificationTestData.Failure(clock, "persistent") with { AutoCloseMs = 0 });
        Assert.HasCount(2, banners); Assert.IsTrue(banners[1].Event.IsPersistent);
    }

    [TestMethod]
    public async Task OpeningAndClosingCenterInvalidatesAlreadyQueuedRecordedBanner()
    {
        var clock = new NotificationTestClock(); var environment = NotificationTestData.Background with { Foreground = true };
        var core = new NotificationCoordinator(clock, () => environment, new NotificationFakeSystem(), new NotificationFakeSound());
        NotificationBannerRequest? request = null; core.BannerRequested += next => request = next;
        await core.PublishAsync(Result(clock, trigger: NotificationTrigger.Manual));
        Assert.IsNotNull(request); Assert.IsTrue(core.CanQueueBanner(request));
        environment = environment with { PresentationGeneration = 1, CenterOpen = true };
        Assert.IsFalse(core.CanShowBanner(request));
        environment = environment with { CenterOpen = false };
        Assert.IsFalse(core.CanQueueBanner(request)); Assert.IsFalse(core.CanShowBanner(request));
    }

    [TestMethod]
    public void OnlyTheVersionActuallyViewedCanBeMarkedRead()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock);
        var first = Result(clock, result: NotificationResultKind.Failed);
        store.Upsert(first); store.MarkRead(first.Id, first.Revision);
        Assert.AreEqual(0, store.ImportantUnreadCount);
        var changed = Result(clock, result: NotificationResultKind.Warning);
        store.Upsert(changed);
        Assert.IsFalse(store.MarkRead(first.Id, first.Revision)); Assert.AreEqual(1, store.ImportantUnreadCount);
        Assert.IsFalse(store.Upsert(Result(clock, result: NotificationResultKind.Warning)));
        Assert.AreEqual(changed.Revision, store.Find(changed.Id)!.Revision);
        store.MarkRead(changed.Id, changed.Revision); Assert.AreEqual(0, store.ImportantUnreadCount);
    }

    [TestMethod]
    public void ActivationMarksOnlyTheRecordVersionItResolved()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock);
        store.Upsert(Result(clock, result: NotificationResultKind.Failed));
        NotificationActivationRouter.Route(new Dictionary<string, string> { ["notification"] = "one" }, store.Find,
            _ => Assert.Fail(), _ => true, _ => false, _ => { }, () => Assert.Fail(), (id, revision) =>
            {
                store.Upsert(Result(clock, result: NotificationResultKind.Warning));
                store.MarkRead(id, revision);
            });
        Assert.AreEqual(1, store.ImportantUnreadCount);
    }

    [TestMethod]
    public void ClearReadPreservesUnreadAndUndoDoesNotOverwriteNewerResults()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock);
        store.Upsert(Result(clock, "read")); store.Upsert(Result(clock, "unread", result: NotificationResultKind.Failed));
        store.MarkRead("read"); Assert.AreEqual(1, store.ClearRead()); Assert.IsNull(store.Find("read"));
        Assert.AreEqual(1, store.ImportantUnreadCount);
        store.Upsert(Result(clock, "read", result: NotificationResultKind.Warning));
        store.UndoClear();
        Assert.IsFalse(store.Find("read")!.IsRead); Assert.AreEqual(NotificationImportance.Important, store.Find("read")!.Importance);
        Assert.AreEqual(2, store.ImportantUnreadCount); Assert.AreEqual(0, store.UndoCount);
    }

    [TestMethod]
    public void UndoRetainsReadStateAndOriginalTimeButExpiresAtTenSeconds()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock);
        store.Upsert(Result(clock)); store.MarkRead("one"); var original = store.Find("one"); store.ClearRead();
        clock.Advance(9999); Assert.AreEqual(1, store.UndoCount); store.UndoClear(); Assert.AreEqual(original, store.Find("one"));
        store.ClearRead(); clock.Advance(10000); Assert.AreEqual(0, store.UndoCount); store.UndoClear(); Assert.IsNull(store.Find("one"));
    }

    [TestMethod]
    public void OnlyMostRecentClearCanBeUndoneAndCapacityStillFavorsNewResults()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock, 2);
        store.Upsert(Result(clock, "first")); store.MarkAllRead(); store.ClearRead();
        clock.Advance(1); store.Upsert(Result(clock, "second")); store.MarkAllRead(); store.ClearRead();
        clock.Advance(1); store.Upsert(Result(clock, "third"));
        clock.Advance(1); store.Upsert(Result(clock, "fourth"));
        store.UndoClear(); Assert.IsNull(store.Find("first")); Assert.IsNull(store.Find("second"));
        Assert.HasCount(2, store.Snapshot()); Assert.AreEqual(2, store.UnreadCount);
    }

    [TestMethod]
    public async Task ClearingDoesNotResurrectDuplicateRecordsOrResetAlertCooldown()
    {
        var clock = new NotificationTestClock(); var system = new NotificationFakeSystem();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background, system, new NotificationFakeSound());
        await core.PublishAsync(NotificationTestData.Failure(clock)); core.MarkAllRead(); core.ClearRead();
        await core.PublishAsync(NotificationTestData.Failure(clock));
        Assert.HasCount(0, core.Store.Snapshot()); Assert.HasCount(1, system.Submissions);
        await core.PublishAsync(NotificationTestData.Failure(clock, "another-operation"));
        Assert.HasCount(1, core.Store.Snapshot()); Assert.HasCount(1, system.Submissions);
        core.UndoClear(); Assert.HasCount(2, core.Store.Snapshot()); Assert.AreEqual(1, core.Store.ImportantUnreadCount);
    }

    [TestMethod]
    public void CenterSnapshotFreezesContentAndCoalescesUpdatesUntilExplicitRefresh()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock); var reading = new NotificationReadingSession();
        store.Upsert(Result(clock, result: NotificationResultKind.Failed)); reading.Open(store.Snapshot());
        Assert.IsTrue(reading.ImportantOnly); reading.SetProtection(true); var first = reading.Visible.Single();
        store.Upsert(Result(clock, result: NotificationResultKind.Warning)); reading.Observe(store.Snapshot());
        store.Upsert(Result(clock, result: NotificationResultKind.Failed)); reading.Observe(store.Snapshot());
        store.Upsert(Result(clock, "other", result: NotificationResultKind.Failed)); reading.Observe(store.Snapshot());
        Assert.AreEqual(2, reading.PendingCount); Assert.AreEqual(first, reading.Visible.Single());
        reading.SetProtection(false);
        Assert.AreEqual(2, reading.PendingCount); Assert.AreEqual(first, reading.Visible.Single(), "Finishing an interaction must not silently apply a pending reading refresh.");
        reading.ApplyPending(); Assert.HasCount(2, reading.Visible); Assert.AreEqual(0, reading.PendingCount);
    }

    [TestMethod]
    public void ActiveReadingReflectsReadActionsWithoutChangingOrderOrAutomaticallyChangingFilter()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock); var reading = new NotificationReadingSession();
        store.Upsert(Result(clock)); reading.Open(store.Snapshot()); Assert.IsFalse(reading.ImportantOnly);
        reading.SetProtection(true); store.MarkAllRead(); reading.Observe(store.Snapshot()); Assert.IsTrue(reading.Visible.Single().IsRead);
        store.Upsert(Result(clock, "error", result: NotificationResultKind.Failed)); reading.Observe(store.Snapshot());
        Assert.IsFalse(reading.ImportantOnly); Assert.AreEqual(1, reading.PendingCount);
        reading.Close(); reading.Open(store.Snapshot()); Assert.IsTrue(reading.ImportantOnly);
        reading.UnreadOnly = true; Assert.AreEqual("error", reading.Visible.Single().Id);
    }

    [TestMethod]
    public void ExplicitClearAndUndoCanRefreshEvenWhileReading()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock); var reading = new NotificationReadingSession();
        store.Upsert(Result(clock)); store.MarkAllRead(); reading.Open(store.Snapshot()); reading.SetProtection(true);
        store.ClearRead(); reading.Observe(store.Snapshot(), true); Assert.HasCount(0, reading.Visible);
        store.UndoClear(); reading.Observe(store.Snapshot(), true); Assert.HasCount(1, reading.Visible); Assert.IsTrue(reading.Visible[0].IsRead);
    }

    [TestMethod]
    public void ClearingRecordedBannersLeavesOrdinaryFeedbackAndIndependentPersistentSurface()
    {
        var clock = new NotificationTestClock(); var temporary = new NotificationScheduler(clock); var persistent = new NotificationScheduler(clock);
        temporary.Enqueue(new(Result(clock), 0, false));
        temporary.Enqueue(new(NotificationTestData.Item(clock, "ordinary", category: NotificationCategory.Transient), 0, false));
        persistent.Enqueue(new(NotificationTestData.Item(clock, "status", category: NotificationCategory.Announcement, duration: 0), 0, false));
        temporary.DiscardRecorded();
        Assert.AreEqual("ordinary", temporary.Current!.Event.Id); Assert.AreEqual("status", persistent.Current!.Event.Id);
    }

    [TestMethod]
    public void PresentationUsesFrozenNamesAndStructuredCountsInsteadOfParsingTheMessage()
    {
        var request = new InAppNotificationRequest { Content = new(NotificationOperationKind.Backup, NotificationTrigger.Manual,
            NotificationResultKind.Success, "secret-project", "folder", new(2, 0, 0, 0)), Message = "unstructured 0 / 999", Severity = NotificationSeverity.Success };
        var item = NotificationEvent.Capture(request, null, new NotificationTestClock(), s => s.Replace("secret", "redacted"));
        request.Content = request.Content with { ObjectName = "changed" };
        string Text(string key) => key switch { "NotificationUI_TitleFormat" => "{0} {1} {2}", "NotificationUI_Count_Created" => "new {0}", _ => key };
        Assert.AreEqual("redacted-project", item.Content!.ProjectName);
        Assert.Contains("folder", NotificationContentFormatter.Title(item.Content, item.Title, Text));
        Assert.AreEqual("new 2", NotificationContentFormatter.Summary(item.Content, item.Message, Text));
        Assert.AreEqual(5000, item.AutoCloseMs);
        Assert.AreEqual(8000, Result(new NotificationTestClock(), result: NotificationResultKind.Warning).AutoCloseMs);
        Assert.AreEqual(12000, Result(new NotificationTestClock(), result: NotificationResultKind.Failed).AutoCloseMs);
    }
}
