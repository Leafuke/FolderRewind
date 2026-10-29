using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

internal sealed class NotificationTestClock : TimeProvider
{
    private long _milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());
    public void Advance(int milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
}

internal static class NotificationTestData
{
    public static NotificationEvent Item(TimeProvider clock, string id = "one", NotificationSeverity severity = NotificationSeverity.Success,
        NotificationImportance importance = NotificationImportance.Info, NotificationCategory category = NotificationCategory.BusinessResult,
        string config = "config-a", int duration = 5000, bool once = false, string message = "result", bool? sound = null)
        => NotificationEvent.Capture(new()
        {
            RecordId = id, Target = new(NotificationTargetKind.Tasks, id, config), Category = category,
            Severity = severity, Title = "title", Message = message, AutoCloseMs = duration,
            AlertOnce = once, CompletionSoundSuccess = sound, FailureCode = "offline", Source = "backup"
        }, importance, clock, text => text);
    public static NotificationEvent Failure(TimeProvider clock, string id = "one", string config = "config-a", bool once = false)
        => Item(clock, id, NotificationSeverity.Error, NotificationImportance.Error, config: config, once: once);
    public static NotificationEnvironment Background => new(true, false, false, ToastNotificationLevel.All, true, 0);
}

internal sealed class NotificationFakeSystem : INotificationSystemSender
{
    public int Calls;
    public List<(NotificationEvent Event, bool Muted)> Submissions { get; } = [];
    public Func<Func<bool>, Task<NotificationSendStatus>>? Handler;
    public async Task<NotificationSendStatus> SendAsync(NotificationEvent item, bool muteAudio, Func<bool> canSend)
    {
        Calls++;
        var result = Handler is null ? canSend() ? NotificationSendStatus.Submitted : NotificationSendStatus.Canceled : await Handler(canSend);
        if (result == NotificationSendStatus.Submitted) Submissions.Add((item, muteAudio));
        return result;
    }
}
internal sealed class NotificationFakeSound : INotificationSoundSender
{
    public int Submissions;
    public Task<NotificationSendStatus> SendAsync(bool success, Func<bool> canSend)
    {
        if (!canSend()) return Task.FromResult(NotificationSendStatus.Canceled);
        Submissions++; return Task.FromResult(NotificationSendStatus.Submitted);
    }
}

[TestClass]
public sealed class NotificationCoreTests
{
    [TestMethod]
    [DataRow(ToastNotificationLevel.Off, NotificationImportance.Info, false)]
    [DataRow(ToastNotificationLevel.Off, NotificationImportance.Error, false)]
    [DataRow(ToastNotificationLevel.ErrorOnly, NotificationImportance.Info, false)]
    [DataRow(ToastNotificationLevel.ErrorOnly, NotificationImportance.Important, false)]
    [DataRow(ToastNotificationLevel.ErrorOnly, NotificationImportance.Error, true)]
    [DataRow(ToastNotificationLevel.ImportantAndAbove, NotificationImportance.Info, false)]
    [DataRow(ToastNotificationLevel.ImportantAndAbove, NotificationImportance.Important, true)]
    [DataRow(ToastNotificationLevel.ImportantAndAbove, NotificationImportance.Error, true)]
    [DataRow(ToastNotificationLevel.All, NotificationImportance.Info, true)]
    [DataRow(ToastNotificationLevel.All, NotificationImportance.Important, true)]
    [DataRow(ToastNotificationLevel.All, NotificationImportance.Error, true)]
    public void OperationRoutingHonorsLevelAndNeverSendsSystemInForeground(ToastNotificationLevel level, NotificationImportance importance, bool system)
    {
        var item = NotificationTestData.Item(new NotificationTestClock(), importance: importance);
        var environment = NotificationTestData.Background with { ToastLevel = level };
        var background = NotificationPolicy.Evaluate(item, environment);
        Assert.AreEqual(system, background.System); Assert.IsFalse(background.Banner); Assert.IsTrue(background.Record);
        var foreground = NotificationPolicy.Evaluate(item, environment with { Foreground = true });
        Assert.IsFalse(foreground.System); Assert.IsTrue(foreground.Banner);
    }

    [TestMethod]
    [DataRow(NotificationCategory.Transient, false, true)]
    [DataRow(NotificationCategory.Progress, false, false)]
    [DataRow(NotificationCategory.Announcement, false, true)]
    [DataRow(NotificationCategory.Cancellation, true, true)]
    public void NonSystemCategoriesStayNonSystemEvenAtAll(NotificationCategory category, bool record, bool banner)
    {
        var item = NotificationTestData.Item(new NotificationTestClock(), category: category, sound: true);
        var plan = NotificationPolicy.Evaluate(item, NotificationTestData.Background with { Foreground = true });
        Assert.AreEqual(record, plan.Record); Assert.AreEqual(banner, plan.Banner);
        Assert.IsFalse(plan.System); Assert.IsFalse(plan.Sound);
        Assert.IsFalse(NotificationPolicy.Evaluate(item, NotificationTestData.Background).System);
    }

    [TestMethod]
    public void DisabledStillRecordsButSuppressionDoesNot()
    {
        var item = NotificationTestData.Failure(new NotificationTestClock());
        Assert.AreEqual(new NotificationDeliveryPlan(true, false, false, false, false),
            NotificationPolicy.Evaluate(item, NotificationTestData.Background with { Enabled = false }));
        Assert.AreEqual(default, NotificationPolicy.Evaluate(item, NotificationTestData.Background with { Suppressed = true }));
    }

    [TestMethod]
    public async Task CompletionSoundMutesSystemAndCancellationNeverRings()
    {
        var clock = new NotificationTestClock(); var system = new NotificationFakeSystem(); var sound = new NotificationFakeSound();
        var environment = NotificationTestData.Background;
        var core = new NotificationCoordinator(clock, () => environment, system, sound);
        await core.PublishAsync(NotificationTestData.Item(clock, sound: true));
        Assert.HasCount(1, system.Submissions); Assert.IsTrue(system.Submissions[0].Muted); Assert.AreEqual(1, sound.Submissions);
        environment = environment with { CompletionSoundEnabled = false };
        await core.PublishAsync(NotificationTestData.Item(clock, "two", sound: true));
        Assert.IsFalse(system.Submissions[1].Muted); Assert.AreEqual(1, sound.Submissions);
        await core.PublishAsync(NotificationTestData.Item(clock, "cancel", category: NotificationCategory.Cancellation, sound: true));
        Assert.HasCount(2, system.Submissions); Assert.AreEqual(1, sound.Submissions); Assert.AreEqual(3, core.Store.UnreadCount);
    }

    [TestMethod]
    public void FailureCooldownBoundaryEscalationAndChannelsAreIndependent()
    {
        var clock = new NotificationTestClock(); var ledger = new NotificationDeliveryLedger(clock);
        var warning = NotificationTestData.Item(clock, severity: NotificationSeverity.Warning, importance: NotificationImportance.Important);
        ledger.Submitted(warning, NotificationChannel.Banner);
        Assert.IsFalse(ledger.Allows(warning, NotificationChannel.Banner));
        Assert.IsTrue(ledger.Allows(warning, NotificationChannel.System));
        Assert.IsTrue(ledger.Allows(NotificationTestData.Failure(clock), NotificationChannel.Banner));
        Assert.IsTrue(ledger.Allows(warning with { FailureKey = "another-configuration" }, NotificationChannel.Banner));
        clock.Advance(29999); Assert.IsFalse(ledger.Allows(warning, NotificationChannel.Banner));
        clock.Advance(1); Assert.IsTrue(ledger.Allows(warning, NotificationChannel.Banner));
    }

    [TestMethod]
    public async Task FailedSendDoesNotConsumeCooldownAndAllOperationsRemainRecorded()
    {
        var clock = new NotificationTestClock(); var system = new NotificationFakeSystem
        { Handler = _ => Task.FromResult(NotificationSendStatus.Failed) };
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background, system, new NotificationFakeSound());
        await core.PublishAsync(NotificationTestData.Failure(clock, "failed"));
        system.Handler = null;
        await core.PublishAsync(NotificationTestData.Failure(clock, "submitted"));
        await core.PublishAsync(NotificationTestData.Failure(clock, "merged"));
        Assert.AreEqual(2, system.Calls); Assert.HasCount(1, system.Submissions); Assert.AreEqual(3, core.Store.UnreadCount);
    }

    [TestMethod]
    public async Task PendingSendRechecksGenerationAndDoesNotBlockRecordUpdates()
    {
        var clock = new NotificationTestClock(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = NotificationTestData.Background;
        var system = new NotificationFakeSystem { Handler = async guard => { await release.Task; return guard() ? NotificationSendStatus.Submitted : NotificationSendStatus.Canceled; } };
        var core = new NotificationCoordinator(clock, () => environment, system, new NotificationFakeSound());
        var old = core.PublishAsync(NotificationTestData.Failure(clock, "old"));
        environment = environment with { Enabled = false, Generation = 1 };
        await core.PublishAsync(NotificationTestData.Failure(clock, "while-off"));
        Assert.AreEqual(2, core.Store.UnreadCount);
        release.SetResult(); await old;
        Assert.HasCount(0, system.Submissions);
        environment = environment with { Enabled = true };
        system.Handler = null;
        await core.PublishAsync(NotificationTestData.Failure(clock, "new"));
        Assert.HasCount(1, system.Submissions);
        await core.PublishAsync(NotificationTestData.Failure(clock, "while-off"));
        Assert.HasCount(1, system.Submissions, "Enabling must not replay an unchanged result recorded while muted.");
    }

    [TestMethod]
    public async Task MovingToForegroundCancelsPendingSystemSubmission()
    {
        var clock = new NotificationTestClock(); var environment = NotificationTestData.Background;
        var system = new NotificationFakeSystem { Handler = guard => { environment = environment with { Foreground = true }; return Task.FromResult(guard() ? NotificationSendStatus.Submitted : NotificationSendStatus.Canceled); } };
        var core = new NotificationCoordinator(clock, () => environment, system, new NotificationFakeSound());
        await core.PublishAsync(NotificationTestData.Failure(clock));
        Assert.HasCount(0, system.Submissions); Assert.AreEqual(1, core.Store.UnreadCount);
    }

    [TestMethod]
    public void StoreEvictsOldestAndReadStateChangesOnlyForNewMeaningfulResults()
    {
        var clock = new NotificationTestClock(); var store = new NotificationStore(clock);
        for (var i = 0; i < 201; i++) { store.Upsert(NotificationTestData.Item(clock, i.ToString())); clock.Advance(1); }
        Assert.HasCount(200, store.Snapshot()); Assert.IsNull(store.Find("0")); Assert.AreEqual(200, store.UnreadCount);
        store.MarkRead("200"); Assert.AreEqual(199, store.UnreadCount);
        Assert.IsFalse(store.Upsert(NotificationTestData.Item(clock, "200"))); Assert.IsTrue(store.Find("200")!.IsRead);
        Assert.IsTrue(store.Upsert(NotificationTestData.Item(clock, "200", message: "changed"))); Assert.AreEqual(200, store.UnreadCount);
        store.MarkAllRead(); Assert.AreEqual(0, store.UnreadCount);
    }

    [TestMethod]
    public void CaptureFreezesMutableRequestsArgumentsAndSanitizesEveryChannelPayload()
    {
        var request = new InAppNotificationRequest { RecordId = "stable", Title = "secret", Message = "secret", Details = "secret", KeepInCenter = true };
        var args = new Dictionary<string, string> { ["target"] = "Tasks" };
        var item = NotificationEvent.Capture(request, null, new NotificationTestClock(), value => value.Replace("secret", "redacted"), arguments: args);
        request.RecordId = "changed"; request.Title = "modified"; request.Message = "modified"; args["target"] = "History";
        Assert.AreEqual("stable", item.Id); Assert.AreEqual("redacted", item.Title);
        Assert.AreEqual("redacted", item.Message); Assert.AreEqual("redacted", item.Details); Assert.AreEqual("Tasks", item.Arguments["target"]);
    }

    [TestMethod]
    public async Task SameBatchUpdatesOneRecordAndOldCloudResultCannotReplaceNewFailure()
    {
        var clock = new NotificationTestClock(); var system = new NotificationFakeSystem();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background, system, new NotificationFakeSound());
        var first = NotificationTestData.Failure(clock, once: true) with { Correlation = new("job-one", "one", clock.GetUtcNow(), clock.GetUtcNow()) };
        await core.PublishAsync(first); core.MarkRead(first.Id);
        clock.Advance(1);
        var final = first with { Revision = "final", Message = "final summary", ResultKey = "final", Correlation = first.Correlation with { EventUtc = clock.GetUtcNow() } };
        await core.PublishAsync(final);
        Assert.HasCount(1, core.Store.Snapshot()); Assert.AreEqual(1, core.Store.UnreadCount); Assert.HasCount(1, system.Submissions);
        await core.PublishAsync(first with { Revision = "late", Message = "old success", ResultKey = "old" });
        Assert.AreEqual("final summary", core.Store.Find(first.Id)!.Message);
        await core.PublishAsync(NotificationTestData.Failure(clock, "other-job") with { Correlation = new("job-two", "two", clock.GetUtcNow(), clock.GetUtcNow()) });
        Assert.HasCount(2, core.Store.Snapshot());
    }

    [TestMethod]
    public async Task ConcurrentPublicationsAndThrowingObserversDoNotLoseRecords()
    {
        var clock = new NotificationTestClock(); var core = new NotificationCoordinator(clock,
            () => NotificationTestData.Background with { Enabled = false }, new NotificationFakeSystem(), new NotificationFakeSound());
        var observed = 0;
        core.Changed += () => throw new InvalidOperationException("observer");
        core.Changed += () => Interlocked.Increment(ref observed);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => core.PublishAsync(NotificationTestData.Item(clock, i.ToString())))));
        Assert.AreEqual(100, observed); Assert.AreEqual(100, core.Store.UnreadCount);
    }

    [TestMethod]
    public async Task DisabledSystemDoesNotFallBackAndOtherFailuresFallBackOnlyOnce()
    {
        var primary = new NotificationFakeSystem { Handler = _ => Task.FromResult(NotificationSendStatus.Disabled) };
        var fallback = new NotificationFakeSystem(); var sender = new FallbackNotificationSender(primary, fallback);
        var item = NotificationTestData.Failure(new NotificationTestClock());
        Assert.AreEqual(NotificationSendStatus.Disabled, await sender.SendAsync(item, true, () => true));
        Assert.AreEqual(0, fallback.Calls);
        primary.Handler = _ => throw new InvalidOperationException("platform");
        fallback.Handler = _ => Task.FromResult(NotificationSendStatus.Failed);
        Assert.AreEqual(NotificationSendStatus.Failed, await sender.SendAsync(item, true, () => true));
        Assert.AreEqual(1, fallback.Calls);
    }

    [TestMethod]
    public void FailureCacheIsBoundedAndExpiredKeysAreRemoved()
    {
        var clock = new NotificationTestClock(); var ledger = new NotificationDeliveryLedger(clock);
        for (var i = 0; i < 2100; i++) { ledger.Submitted(NotificationTestData.Failure(clock, config: i.ToString()), NotificationChannel.System); clock.Advance(1); }
        Assert.IsLessThanOrEqualTo(2048, ledger.FailureCount);
        clock.Advance(30000); Assert.IsTrue(ledger.Allows(NotificationTestData.Failure(clock), NotificationChannel.System));
        Assert.AreEqual(0, ledger.FailureCount);
    }

    [TestMethod]
    public async Task FailedSystemSubmissionStillAllowsConfiguredCompletionSoundWithoutRepeatingIt()
    {
        var clock = new NotificationTestClock();
        var system = new NotificationFakeSystem { Handler = _ => Task.FromResult(NotificationSendStatus.Failed) };
        var sound = new NotificationFakeSound();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background, system, sound);
        await core.PublishAsync(NotificationTestData.Failure(clock, "first") with { CompletionSoundSuccess = false });
        await core.PublishAsync(NotificationTestData.Failure(clock, "second") with { CompletionSoundSuccess = false });
        Assert.AreEqual(2, system.Calls, "Failed system attempts must not consume their channel quota.");
        Assert.AreEqual(1, sound.Submissions, "The successful sound uses its own cooldown.");
    }

    [TestMethod]
    public async Task ForegroundFailureMergeDoesNotRingAgainAndBackgroundHasIndependentBudget()
    {
        var clock = new NotificationTestClock(); var system = new NotificationFakeSystem(); var sound = new NotificationFakeSound();
        var environment = NotificationTestData.Background with { Foreground = true };
        var core = new NotificationCoordinator(clock, () => environment, system, sound);
        var shown = 0;
        core.BannerRequested += request =>
        {
            if (!request.QuietUpdate && core.CanShowBanner(request)) { shown++; core.ReportBannerShown(request); }
        };
        await core.PublishAsync(NotificationTestData.Failure(clock, "first") with { CompletionSoundSuccess = false });
        await core.PublishAsync(NotificationTestData.Failure(clock, "second") with { CompletionSoundSuccess = false });
        Assert.AreEqual(1, shown); Assert.AreEqual(1, sound.Submissions);
        environment = environment with { Foreground = false };
        await core.PublishAsync(NotificationTestData.Failure(clock, "third") with { CompletionSoundSuccess = false });
        Assert.HasCount(1, system.Submissions); Assert.AreEqual(1, sound.Submissions);
        Assert.AreEqual(3, core.Store.UnreadCount);
    }

    [TestMethod]
    public async Task AcknowledgmentRaceStillUpdatesTheVisibleOperationWithoutANewAlert()
    {
        var clock = new NotificationTestClock(); var requests = new List<NotificationBannerRequest>();
        var core = new NotificationCoordinator(clock, () => NotificationTestData.Background with { Foreground = true },
            new NotificationFakeSystem(), new NotificationFakeSound());
        core.BannerRequested += requests.Add;
        await core.PublishAsync(NotificationTestData.Failure(clock, "batch", once: true));
        await core.PublishAsync(NotificationTestData.Failure(clock, "batch", once: true) with { Message = "final", ResultKey = "final" });
        core.ReportBannerShown(requests[0]);
        Assert.IsNull(core.PrepareBanner(requests[1], false));
        var replacement = core.PrepareBanner(requests[1], true);
        Assert.IsNotNull(replacement); Assert.IsTrue(replacement.QuietUpdate); Assert.AreEqual("final", replacement.Event.Message);
    }

    [TestMethod]
    public async Task SoundChoiceCannotChangeIntoTwoSoundsDuringSubmission()
    {
        var clock = new NotificationTestClock(); var environment = NotificationTestData.Background with { CompletionSoundEnabled = false };
        var system = new NotificationFakeSystem { Handler = _ => { environment = environment with { CompletionSoundEnabled = true }; return Task.FromResult(NotificationSendStatus.Submitted); } };
        var sound = new NotificationFakeSound(); var core = new NotificationCoordinator(clock, () => environment, system, sound);
        await core.PublishAsync(NotificationTestData.Item(clock, sound: true));
        Assert.IsFalse(system.Submissions.Single().Muted); Assert.AreEqual(0, sound.Submissions);
    }

    [TestMethod]
    public async Task DisablingOnlySystemAlertsDuringSubmissionPreservesConfiguredCompletionSound()
    {
        var clock = new NotificationTestClock(); var environment = NotificationTestData.Background;
        var system = new NotificationFakeSystem
        {
            Handler = guard =>
            {
                environment = environment with { ToastLevel = ToastNotificationLevel.Off };
                return Task.FromResult(guard() ? NotificationSendStatus.Submitted : NotificationSendStatus.Canceled);
            }
        };
        var sound = new NotificationFakeSound();
        var core = new NotificationCoordinator(clock, () => environment, system, sound);
        await core.PublishAsync(NotificationTestData.Item(clock, sound: true));
        Assert.HasCount(0, system.Submissions); Assert.AreEqual(1, sound.Submissions);
    }

    [TestMethod]
    public async Task FallbackRechecksPermissionAfterPrimaryFailure()
    {
        var allowed = true;
        var primary = new NotificationFakeSystem { Handler = _ => { allowed = false; return Task.FromResult(NotificationSendStatus.Failed); } };
        var fallback = new NotificationFakeSystem();
        var result = await new FallbackNotificationSender(primary, fallback).SendAsync(NotificationTestData.Failure(new NotificationTestClock()), true, () => allowed);
        Assert.AreEqual(NotificationSendStatus.Canceled, result); Assert.AreEqual(0, fallback.Calls);
    }
}
