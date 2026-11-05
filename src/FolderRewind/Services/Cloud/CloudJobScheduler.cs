using FolderRewind.History.Cloud;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>A capacity-one hint; durable journal reads remain the only execution authority.</summary>
internal sealed class CloudJobWakeSignal
{
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });

    public void Set() => _signals.Writer.TryWrite(0);
    public bool TryConsume() => _signals.Reader.TryRead(out _);

    public async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Persisted timestamps can be arbitrarily far in the future; keep within timer limits.
        deadline.CancelAfter(delay < TimeSpan.FromDays(20) ? delay : TimeSpan.FromDays(20));
        try { await _signals.Reader.ReadAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }
}

internal readonly record struct CloudJobSchedule(HistoryCloudJob? ReadyJob, DateTimeOffset? NextEligibleUtc)
{
    public static CloudJobSchedule Select(IEnumerable<HistoryCloudJob> jobs, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var job in jobs)
        {
            if (job.Protection != CloudProtectionDisposition.Held
                || job.Phase is not (CloudJobPhase.Pending or CloudJobPhase.RetryableFailure)) continue;
            if (job.NextEligibleUtc <= now) return new(job, null);
            if (next is null || job.NextEligibleUtc < next) next = job.NextEligibleUtc;
        }
        return new(null, next);
    }
}

internal static class CloudJobAttempt
{
    // Caller owns MutationGate. Publish may synchronously wake an already running worker.
    public static void RequestRetry(HistoryCloudJobJournal journal, HistoryCloudJob job,
        NotificationExecutionOrigins origins, DateTimeOffset now)
    {
        origins.RequestManual(job.JobId);
        journal.Update(job with { Phase = CloudJobPhase.Pending, NextEligibleUtc = now, Diagnostic = "" });
    }

    // Consume origin only after a fresh journal read under MutationGate authorizes this attempt.
    public static NotificationTrigger? TakeTrigger(HistoryCloudJob job, bool scheduled, DateTimeOffset now,
        NotificationExecutionOrigins origins)
    {
        if (job.Protection != CloudProtectionDisposition.Held
            || job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Succeeded or CloudJobPhase.Aborted
            || scheduled && (job.Phase is not (CloudJobPhase.Pending or CloudJobPhase.RetryableFailure) || job.NextEligibleUtc > now))
            return null;
        return origins.Take(job.JobId, job.Automatic);
    }
}

internal static class CloudJobScheduler
{
    public static async Task RunAsync(Func<HistoryCloudJournalDocument> read,
        Func<HistoryCloudJob, CancellationToken, Task> execute,
        CloudJobWakeSignal wake, Func<bool> stopping, CancellationToken token)
    {
        while (!stopping())
        {
            token.ThrowIfCancellationRequested();
            wake.TryConsume();
            // Never retain a journal snapshot, targets, or manifests while waiting.
            var schedule = CloudJobSchedule.Select(read().Jobs, DateTimeOffset.UtcNow);
            var readyJob = schedule.ReadyJob;
            var deadline = schedule.NextEligibleUtc;
            schedule = default;
            if (readyJob is not null)
            {
                await execute(readyJob, token).ConfigureAwait(false);
                readyJob = null;
            }
            else if (deadline is { } next)
                await wake.WaitAsync(next - DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            else return;
        }
    }
}
