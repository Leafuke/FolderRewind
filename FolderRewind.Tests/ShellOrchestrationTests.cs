using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ShellOrchestrationTests
{
    [TestMethod]
    public async Task StartupRunsOnceAndContinuesAfterStageFailure()
    {
        var errors = new List<Exception>();
        var sequence = new StartupSequence(errors.Add);
        var calls = 0;
        Func<CancellationToken, Task>[] stages = [ _ => throw new IOException("failed"), _ => { calls++; return Task.CompletedTask; } ];
        await sequence.RunAsync(stages, default);
        await sequence.RunAsync(stages, default);
        Assert.AreEqual(1, calls);
        Assert.HasCount(1, errors);
    }

    [TestMethod]
    public async Task UnloadCancelsStartupWithoutLaterStages()
    {
        using var cancellation = new CancellationTokenSource();
        var sequence = new StartupSequence(_ => Assert.Fail());
        var task = sequence.RunAsync([token => Task.Delay(Timeout.InfiniteTimeSpan, token), _ => throw new Exception("later stage")], cancellation.Token);
        cancellation.Cancel();
        await task;
    }

    [TestMethod]
    public void PriorityNotificationPreservesRemainingFifoOrder()
    {
        var queue = new PriorityNotificationQueue<string>();
        queue.Enqueue("first"); queue.Enqueue("second"); queue.EnqueueFirst("error");
        Assert.IsTrue(queue.TryDequeue(out var item)); Assert.AreEqual("error", item);
        Assert.IsTrue(queue.TryDequeue(out item)); Assert.AreEqual("first", item);
        Assert.IsTrue(queue.TryDequeue(out item)); Assert.AreEqual("second", item);
        Assert.IsFalse(queue.TryDequeue(out _));
    }

    [TestMethod]
    public void HoverPausesRemainingDurationWithoutCountingHoverTime()
    {
        var time = new ManualTime();
        var countdown = new NotificationCountdown(time);
        countdown.Start(6000);
        time.Timestamp += 2000;
        countdown.Pause();
        Assert.AreEqual(4000, countdown.RemainingMilliseconds);
        time.Timestamp += 50000;
        countdown.Resume();
        time.Timestamp += 1000;
        countdown.Pause();
        Assert.AreEqual(3000, countdown.RemainingMilliseconds);
    }

    [TestMethod]
    public void PersistentNotificationDoesNotAcquireTimeoutOnHover()
    {
        var countdown = new NotificationCountdown(new ManualTime());
        countdown.Start(0); countdown.Pause(); countdown.Resume();
        Assert.AreEqual(0, countdown.RemainingMilliseconds);
    }

    [TestMethod]
    public void ForcedExitDoesNotConsultSettingsOrShowDialog()
    {
        var controller = new WindowCloseController(() => throw new Exception(), () => throw new Exception(), _ => throw new Exception(), () => Assert.Fail(), () => Assert.Fail(), _ => Assert.Fail());
        Assert.IsFalse(controller.HandleClosing(true));
    }

    [TestMethod]
    public async Task RepeatedClosingShowsOnlyOneDialogAndCancelKeepsWindow()
    {
        var answer = new TaskCompletionSource<WindowCloseAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asks = 0;
        var controller = new WindowCloseController(() => null, () => { asks++; return answer.Task; }, _ => throw new Exception(), () => Assert.Fail(), () => Assert.Fail(), _ => Assert.Fail());
        Assert.IsTrue(controller.HandleClosing(false));
        Assert.IsTrue(controller.HandleClosing(false));
        answer.SetResult(new(WindowCloseChoice.Cancel, true));
        await controller.PendingTask;
        Assert.AreEqual(1, asks);
    }

    [TestMethod]
    public async Task RememberedExitSavesBeforeAllowingClose()
    {
        var steps = new List<string>();
        var controller = new WindowCloseController(() => null, () => Task.FromResult(new WindowCloseAnswer(WindowCloseChoice.Exit, true)),
            _ => { steps.Add("save"); return Task.CompletedTask; }, () => Assert.Fail(), () => steps.Add("close"), _ => Assert.Fail());
        Assert.IsTrue(controller.HandleClosing(false));
        await controller.PendingTask;
        CollectionAssert.AreEqual(new[] { "save", "close" }, steps);
        Assert.IsFalse(controller.HandleClosing(false));
    }

    [TestMethod]
    public async Task SaveFailureLeavesWindowOpenAndReportsError()
    {
        var errors = new List<Exception>();
        var controller = new WindowCloseController(() => null, () => Task.FromResult(new WindowCloseAnswer(WindowCloseChoice.Hide, true)),
            _ => throw new IOException("disk full"), () => Assert.Fail(), () => Assert.Fail(), errors.Add);
        Assert.IsTrue(controller.HandleClosing(false));
        await controller.PendingTask;
        Assert.HasCount(1, errors);
    }

    [TestMethod]
    public void RememberedHideDoesNotShowDialog()
    {
        var hidden = false;
        var controller = new WindowCloseController(() => WindowCloseChoice.Hide, () => throw new Exception(), _ => throw new Exception(), () => hidden = true, () => Assert.Fail(), _ => Assert.Fail());
        Assert.IsTrue(controller.HandleClosing(false));
        Assert.IsTrue(hidden);
    }

    [TestMethod]
    public void ShellCodeBehindOnlyOwnsNavigationAndPresentationAdapters()
    {
        var source = File.ReadAllText(Path.Combine(FolderManagerMvvmArchitectureTests.FindRoot(), "FolderRewind/Views/ShellPage.xaml.cs"));
        foreach (var forbidden in new[] { "ConfigService", "NoticeService", "AppUpdateService", "NotificationService", "KnotLinkServerManagerService", "Task.Run(", "new ContentDialog" })
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }
}
