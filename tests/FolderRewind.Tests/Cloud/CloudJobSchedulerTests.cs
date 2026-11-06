using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Text.Json;
using FolderRewind.Models;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudJobSchedulerTests
{
    [TestMethod]
    public void WaitingScheduleKeepsOnlyNearestEligibleDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var later = Job(now.AddHours(2));
        var nearest = Job(now.AddHours(1));
        var canceled = Job(now.AddMinutes(-1)) with { Phase = CloudJobPhase.Canceled };
        var released = Job(now.AddMinutes(-1)) with { Protection = CloudProtectionDisposition.Released };
        var schedule = CloudJobSchedule.Select([later, canceled, nearest, released], now);
        Assert.IsNull(schedule.ReadyJob);
        Assert.AreEqual(nearest.NextEligibleUtc, schedule.NextEligibleUtc);
        Assert.AreSame(nearest, CloudJobSchedule.Select([nearest], nearest.NextEligibleUtc).ReadyJob);
    }

    [TestMethod]
    public async Task IdleRetryReadsOnceAndCancellationDoesNotLeaveAReader()
    {
        var wake = new CloudJobWakeSignal();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var worker = CloudJobScheduler.RunAsync(() => { reads++; return Document(Job(DateTimeOffset.UtcNow.AddHours(1))); },
            (_, _) => throw new AssertFailedException("A future job must not execute."), wake, () => false, cancellation.Token);
        Assert.AreEqual(1, reads);
        Assert.IsFalse(worker.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => worker);
        Assert.AreEqual(1, reads);
        wake.Set();
        await wake.WaitAsync(TimeSpan.FromHours(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(wake.TryConsume());
    }

    [TestMethod]
    public async Task RetryWaitDoesNotRetainTheJournalJobOrItsPayloadEvidence()
    {
        var wake = new CloudJobWakeSignal();
        using var cancellation = new CancellationTokenSource();
        WeakReference<HistoryCloudJob>? observed = null;
        var worker = CloudJobScheduler.RunAsync(() =>
        {
            var job = Job(DateTimeOffset.UtcNow.AddHours(1));
            observed = new(job);
            return Document(job);
        }, (_, _) => throw new AssertFailedException("A future job must not execute."), wake, () => false, cancellation.Token);
        try
        {
            Assert.IsNotNull(observed);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.IsFalse(observed.TryGetTarget(out _), "Only the retry timestamp may be retained during a wait.");
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => worker);
        }
    }

    [TestMethod]
    public async Task ChangeBetweenReadAndWaitIsNotLostAndUsesNewDocument()
    {
        var wake = new CloudJobWakeSignal();
        var reads = 0;
        var stopped = false;
        var newJob = Job(DateTimeOffset.UtcNow.AddSeconds(-1));
        var executed = new List<string>();
        await CloudJobScheduler.RunAsync(() =>
        {
            if (++reads == 1) { wake.Set(); return Document(Job(DateTimeOffset.UtcNow.AddHours(1))); }
            return Document(newJob);
        }, (job, _) => { executed.Add(job.JobId); stopped = true; return Task.CompletedTask; },
            wake, () => stopped, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, reads);
        CollectionAssert.AreEqual(new[] { newJob.JobId }, executed);
    }

    [TestMethod]
    public async Task StopWakesFutureRetryWithoutAnotherJournalRead()
    {
        var wake = new CloudJobWakeSignal();
        var stopped = false;
        var reads = 0;
        var worker = CloudJobScheduler.RunAsync(() => { reads++; return Document(Job(DateTimeOffset.UtcNow.AddDays(1))); },
            (_, _) => throw new AssertFailedException("No job is due."), wake, () => stopped, CancellationToken.None);
        stopped = true;
        wake.Set();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public void WakeBurstCoalescesToOneHint()
    {
        var wake = new CloudJobWakeSignal();
        for (var index = 0; index < 10000; index++) wake.Set();
        Assert.IsTrue(wake.TryConsume());
        Assert.IsFalse(wake.TryConsume());
    }

    [TestMethod]
    public void SkippedSchedulingDoesNotConsumeManualOrigin()
    {
        var origins = new NotificationExecutionOrigins(TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var job = Job(now);
        origins.RequestManual(job.JobId);
        foreach (var skipped in new[] { job with { Phase = CloudJobPhase.Paused }, job with { Phase = CloudJobPhase.Canceled },
            job with { NextEligibleUtc = now.AddHours(1) }, job with { Protection = CloudProtectionDisposition.Released } })
            Assert.IsNull(CloudJobAttempt.TakeTrigger(skipped, true, now, origins));
        Assert.AreEqual(NotificationTrigger.Manual, CloudJobAttempt.TakeTrigger(job, true, now, origins));
        Assert.AreEqual(NotificationTrigger.Automatic, CloudJobAttempt.TakeTrigger(job, true, now, origins));
    }

    [TestMethod]
    public async Task ManualOriginIsRegisteredBeforePendingJournalChangeWakesObservers()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindManualCloudRetry", Guid.NewGuid().ToString("N"));
        try
        {
            var project = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            await using var runtime = new HistoryRuntime(new FileHistoryRepository(project, new(root)));
            await runtime.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var job = Job(now) with { ProjectId = project.Value, Phase = CloudJobPhase.NeedsAttention,
                Target = new("profile", "revision", "binding", "remote:backup", project.Value) };
            var path = runtime.CloudJobs.JournalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Document(job), CloudJobJsonContext.Default.HistoryCloudJournalDocument));
            var origins = new NotificationExecutionOrigins(TimeProvider.System);
            NotificationTrigger? observed = null;
            using var subscription = runtime.ChangeFeed.Subscribe(change =>
            {
                if (change.Kind == HistoryChangeKind.CloudJobChanged)
                    observed = CloudJobAttempt.TakeTrigger(runtime.CloudJobs.Read().Jobs.Single(), true, now, origins);
            });
            await using (var gate = await runtime.MutationGate.EnterAsync(default))
                CloudJobAttempt.RequestRetry(runtime.CloudJobs, job, origins, now);
            Assert.AreEqual(NotificationTrigger.Manual, observed);
            var saved = runtime.CloudJobs.Read().Jobs.Single();
            Assert.AreEqual(CloudProtectionDisposition.Held, saved.Protection);
            Assert.AreEqual(job.Target, saved.Target); Assert.AreEqual(job.Attempt, saved.Attempt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task JournalFailureIsNotReplacedWithCachedProtection()
    {
        var wake = new CloudJobWakeSignal();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CloudJobScheduler.RunAsync(
            () => throw new InvalidDataException("journal missing"), (_, _) => Task.CompletedTask,
            wake, () => false, CancellationToken.None));
    }

    [TestMethod]
    public async Task MissingRequiredJournalWhileWaitingPreventsExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindCloudSchedule", Guid.NewGuid().ToString("N"));
        try
        {
            var project = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            await using var runtime = new HistoryRuntime(new FileHistoryRepository(project, new(root)));
            await runtime.InitializeAsync();
            var job = Job(DateTimeOffset.UtcNow.AddHours(1)) with { ProjectId = project.Value,
                Target = new("profile", "revision", "binding", "remote:backup", project.Value) };
            var path = runtime.CloudJobs.JournalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Document(job), CloudJobJsonContext.Default.HistoryCloudJournalDocument));
            File.WriteAllText(path + ".required", "required");
            var wake = new CloudJobWakeSignal();
            var executed = false;
            var worker = CloudJobScheduler.RunAsync(runtime.CloudJobs.Read, (_, _) => { executed = true; return Task.CompletedTask; },
                wake, () => false, CancellationToken.None);
            Assert.IsFalse(worker.IsCompleted);
            File.Delete(path);
            wake.Set();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => worker.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(executed);
            Assert.IsTrue(File.Exists(path + ".required"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static HistoryCloudJournalDocument Document(params HistoryCloudJob[] jobs) => new(1, 1, [], [.. jobs]);
    private static HistoryCloudJob Job(DateTimeOffset due) => new(Guid.NewGuid().ToString("N"), "project", "admission",
        PackId.New(), HistoryTransactionId.New(), new("profile", "revision", "binding", "remote:backup", "project"),
        [], [], CloudJobPhase.Pending, CloudProtectionDisposition.Held, 0, due, "", DateTimeOffset.UtcNow, true);
}
