using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass, DoNotParallelize]
public sealed class CloudJournalPerformanceBenchmarkTests
{
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasureCloudJournalScaleAndIdleScheduling()
    {
        var outputParent = Environment.GetEnvironmentVariable("FOLDERREWIND_CLOUD_JOURNAL_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputParent)) Assert.Inconclusive("Set FOLDERREWIND_CLOUD_JOURNAL_OUTPUT for isolated cloud journal measurements.");
        var sizes = (Environment.GetEnvironmentVariable("FOLDERREWIND_CLOUD_JOURNAL_SIZES") ?? "100,1000,5000")
            .Split(',').Select(int.Parse).ToArray();
        Assert.IsTrue(sizes.Length > 0 && sizes.All(size => size is >= 1 and <= 5000));
        var repeats = int.Parse(Environment.GetEnvironmentVariable("FOLDERREWIND_CLOUD_JOURNAL_REPEATS") ?? "3");
        Assert.IsTrue(repeats is >= 1 and <= 20);
        var idleMs = int.Parse(Environment.GetEnvironmentVariable("FOLDERREWIND_CLOUD_JOURNAL_IDLE_MS") ?? "2200");
        Assert.IsTrue(idleMs is >= 1100 and <= 10000);
        var output = Path.Combine(Path.GetFullPath(outputParent), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var measurements = new List<object>();
        var fixtures = new List<object>();
        var validations = new List<object>();

        foreach (var size in sizes)
        {
            var root = Path.Combine(output, "jobs-" + size);
            var config = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            await using var runtime = new HistoryRuntime(new FileHistoryRepository(config, new(root)));
            await runtime.InitializeAsync();
            var jobs = Enumerable.Range(0, size).Select(index => CreateJob(index, config)).ToImmutableArray();
            // Seed only a newly-created private fixture; every measured read/write uses production APIs.
            var seed = JsonSerializer.SerializeToUtf8Bytes(new HistoryCloudJournalDocument(1, 1, [], jobs),
                CloudJobJsonContext.Default.HistoryCloudJournalDocument);
            await File.WriteAllBytesAsync(runtime.CloudJobs.JournalPath, seed);
            await File.WriteAllBytesAsync(runtime.CloudJobs.JournalPath + ".required", [1]);
            Assert.IsLessThanOrEqualTo(16 * 1024 * 1024, seed.Length);
            var warm = runtime.CloudJobs.Read();
            Assert.HasCount(size, warm.Jobs);
            fixtures.Add(new { size, initialBytes = seed.Length, initialSha256 = Convert.ToHexString(SHA256.HashData(seed)),
                payload = "Two representations and two immutable manifest intents per job; 20% succeeded/released and 80% retryable/held, all eligible deadlines initially one hour away" });

            for (var repeat = 0; repeat < repeats; repeat++)
            {
                await MeasureAsync("journal-read", repeat, () =>
                {
                    var document = runtime.CloudJobs.Read();
                    return Task.FromResult<object>(new { document.Revision, tasks = document.Jobs.Length });
                });
                var forSelect = runtime.CloudJobs.Read();
                await MeasureAsync("select-nearest-from-already-read-document", repeat, () =>
                {
                    var schedule = CloudJobSchedule.Select(forSelect.Jobs, DateTimeOffset.UtcNow);
                    Assert.IsNull(schedule.ReadyJob); Assert.IsNotNull(schedule.NextEligibleUtc);
                    return Task.FromResult<object>(new { schedule.NextEligibleUtc });
                });
                var current = runtime.CloudJobs.Read().Jobs[0];
                await MeasureAsync("journal-update-read-validate-atomic-rewrite", repeat, async () =>
                {
                    await using var gate = await runtime.MutationGate.EnterAsync(default);
                    runtime.CloudJobs.Update(current with { Attempt = current.Attempt + 1, Diagnostic = "benchmark-retry-" + repeat });
                    return new { changedJob = current.JobId };
                });
                var after = runtime.CloudJobs.Read();
                Assert.AreEqual(current.Attempt + 1, after.Jobs[0].Attempt);
                Assert.AreEqual(warm.Revision + repeat + 1, after.Revision);
                Assert.HasCount(size, after.Jobs);
            }

            foreach (var strategy in Array.IndexOf(sizes, size) % 2 == 0
                ? new[] { "legacy-poll", "deadline-wait" } : new[] { "deadline-wait", "legacy-poll" })
                await MeasureAsync("idle-" + strategy, 0, () => MeasureIdleAsync(runtime.CloudJobs, strategy, idleMs));
            await MeasureAsync("change-feed-wakes-earlier-request", 0, () => MeasureDispatchAsync(runtime, changeFeedWake: true));
            await MeasureAsync("nearest-deadline-without-change-feed-wake", 0, () => MeasureDispatchAsync(runtime, changeFeedWake: false));

            var final = runtime.CloudJobs.Read();
            Assert.HasCount(size, final.Jobs);
            Assert.IsTrue(File.Exists(runtime.CloudJobs.JournalPath + ".required"));
            Assert.IsFalse(File.Exists(runtime.CloudJobs.JournalPath + ".attention"));
            // All other rows and the immutable evidence on the changed row must survive rewrites exactly.
            for (var index = 0; index < size; index++)
            {
                var actual = final.Jobs[index]; var expected = jobs[index];
                if (index == 0) actual = actual with { Phase = expected.Phase, Attempt = expected.Attempt,
                    NextEligibleUtc = expected.NextEligibleUtc, Diagnostic = expected.Diagnostic, UpdatedUtc = expected.UpdatedUtc };
                CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(expected, CloudJobJsonContext.Default.HistoryCloudJob),
                    JsonSerializer.SerializeToUtf8Bytes(actual, CloudJobJsonContext.Default.HistoryCloudJob));
            }
            validations.Add(new { size, tasks = final.Jobs.Length, final.Revision, immutableEvidenceEqual = true,
                requiredEvidencePresent = true, attentionEvidenceAbsent = true });
            Save();
            Console.WriteLine($"Cloud journal jobs={size}: immutable evidence matches; results={Path.Combine(output, "cloud-journal-stages.json")}");

            async Task MeasureAsync(string stage, int repeat, Func<Task<object>> action)
            {
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var diagnostic = HistoryOperationDiagnostics.Begin(stage);
                var clock = Stopwatch.StartNew();
                object detail;
                using (diagnostic) detail = await action();
                clock.Stop(); process.Refresh();
                measurements.Add(new { size, stage, repeat, elapsedMs = clock.Elapsed.TotalMilliseconds,
                    cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
                    gcCollections = Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collections[generation]).ToArray(),
                    journalBytes = new FileInfo(runtime.CloudJobs.JournalPath).Length, diagnostic = diagnostic.Summary, detail });
                Save();
            }
        }

        void Save() => File.WriteAllText(Path.Combine(output, "cloud-journal-stages.json"), JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(), sizes, repeats, idleMilliseconds = idleMs,
            scope = "Current production journal serialization/validation/atomic write plus scheduling strategy comparison; legacy loop reproduces two reads per 1s polling iteration. Not old/new binary A/B, remote upload, Host notification/UI, or retained-memory measurement.",
            cache = "One warm journal read per fixture, uncontrolled OS cache, no forced GC; alternating idle-strategy order by task count",
            io = "Diagnostic host-os-read/write-bytes are process logical I/O, not physical disk bytes. Allocation includes harness/coordinator/tasks and is not retained heap.",
            correctness = "Every unmodified typed job and every immutable field/representation/manifest/protection on the changed job compared to seed outside measurements; execution authorization fresh-reads under MutationGate; required evidence retained",
            fixtures, measurements, validations
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<object> MeasureIdleAsync(HistoryCloudJobJournal journal, string strategy, int idleMs)
    {
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        HistoryCloudJournalDocument Read() { Interlocked.Increment(ref reads); return journal.Read(); }
        var worker = strategy == "legacy-poll" ? LegacyPollAsync(Read, cancellation.Token)
            : CloudJobScheduler.RunAsync(Read, (_, _) => throw new AssertFailedException("A future retry must not execute."),
                new(), () => false, cancellation.Token);
        try { await Task.Delay(idleMs); }
        finally
        {
            cancellation.Cancel();
            try { await worker; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        if (strategy == "deadline-wait") Assert.AreEqual(1, reads);
        else Assert.IsGreaterThanOrEqualTo(2, reads);
        return new { journalReads = reads, executed = 0, observationMilliseconds = idleMs };
    }

    private static async Task LegacyPollAsync(Func<HistoryCloudJournalDocument> read, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var job = read().Jobs.FirstOrDefault(job => job.Protection == CloudProtectionDisposition.Held
                && (job.Phase is CloudJobPhase.Pending or CloudJobPhase.RetryableFailure) && job.NextEligibleUtc <= DateTimeOffset.UtcNow);
            if (job is not null) throw new AssertFailedException("The legacy idle fixture contains a due job.");
            if (!read().Jobs.Any(job => job.Protection == CloudProtectionDisposition.Held
                && (job.Phase is CloudJobPhase.Pending or CloudJobPhase.RetryableFailure))) return;
            await Task.Delay(1000, token);
        }
    }

    private static async Task<object> MeasureDispatchAsync(HistoryRuntime runtime, bool changeFeedWake)
    {
        var journal = runtime.CloudJobs;
        var before = journal.Read().Jobs[0];
        var due = DateTimeOffset.UtcNow.Add(changeFeedWake ? TimeSpan.FromHours(1) : TimeSpan.FromMilliseconds(750));
        await using (var gate = await runtime.MutationGate.EnterAsync(default))
            journal.Update(before with { Phase = CloudJobPhase.Pending, NextEligibleUtc = due });
        using var cancellation = new CancellationTokenSource();
        var wake = new CloudJobWakeSignal();
        int reads = 0, freshReads = 0, stopping = 0, executed = 0;
        DateTimeOffset? executedAt = null;
        using var subscription = runtime.ChangeFeed.Subscribe(change =>
        { if (changeFeedWake && change.Kind == HistoryChangeKind.CloudJobChanged) wake.Set(); });
        var clock = Stopwatch.StartNew();
        var worker = CloudJobScheduler.RunAsync(() => { Interlocked.Increment(ref reads); return journal.Read(); }, async (scheduled, token) =>
        {
            await using var gate = await runtime.MutationGate.EnterAsync(token);
            Interlocked.Increment(ref freshReads);
            var current = journal.Read().Jobs.Single(job => job.JobId == scheduled.JobId);
            Assert.AreEqual(before.JobId, current.JobId);
            Assert.AreEqual(CloudProtectionDisposition.Held, current.Protection);
            Assert.AreEqual(CloudJobPhase.Pending, current.Phase);
            Assert.IsTrue(current.NextEligibleUtc <= DateTimeOffset.UtcNow);
            executedAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref executed); Volatile.Write(ref stopping, 1);
        }, wake, () => Volatile.Read(ref stopping) != 0, cancellation.Token);
        try
        {
            if (changeFeedWake)
            {
                await using var gate = await runtime.MutationGate.EnterAsync(default);
                journal.Update(journal.Read().Jobs[0] with { NextEligibleUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
            }
            await worker.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            cancellation.Cancel();
            try { await worker; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Assert.AreEqual(1, executed); Assert.AreEqual(1, freshReads);
        return new { journalReads = reads, freshAuthorizationReads = freshReads, executed,
            scheduleToAttemptMs = clock.Elapsed.TotalMilliseconds,
            deadlineOvershootMs = changeFeedWake ? (double?)null : (executedAt!.Value - due).TotalMilliseconds };
    }

    private static HistoryCloudJob CreateJob(int index, HistoryConfigId config)
    {
        var target = new CloudJobTarget("fixture-profile", "profile-r1", "binding-r1", "remote:isolated-fixture", config.Value,
            "credential-target-r1", new string('a', 64), "credential-r1", "fixture-working-directory");
        var representations = Enumerable.Range(0, 2).Select(item => RepresentationId.FromGuid(Id("representation", index * 2 + item))).ToImmutableArray();
        var intents = representations.Select((representation, item) =>
        {
            var replica = ReplicaId.FromGuid(Id("replica", index * 2 + item));
            return new CloudUploadIntent(representation, replica,
                new(representation, replica, null, HistoryRepositoryPaths.CreateReplicaObjectKey(replica), 4096, new string('b', 64)));
        }).ToImmutableArray();
        var terminal = index % 5 == 4;
        return new(Id("job", index).ToString("N"), config.Value, "manual", PackId.FromGuid(Id("pack", index)),
            HistoryTransactionId.FromGuid(Id("transaction", index)), target, representations, intents,
            terminal ? CloudJobPhase.Succeeded : CloudJobPhase.RetryableFailure,
            terminal ? CloudProtectionDisposition.Released : CloudProtectionDisposition.Held, 1,
            DateTimeOffset.UtcNow.AddHours(1).AddSeconds(index), "fixture-only", DateTimeOffset.UnixEpoch, true);
    }

    private static Guid Id(string kind, int number) => new(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "/" + number)).AsSpan(0, 16));
}
