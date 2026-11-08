using FolderRewind.Models;
using FolderRewind.Services;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LogPersistenceWriterTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "FolderRewind-log-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private LogEntry Entry(string message) => new() { Timestamp = DateTime.UtcNow, Message = message };
    private static string ReadShared(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private string ReadLogs() => string.Join("\n", Directory.GetFiles(_root, "app-*.log").Select(ReadShared));

    [TestMethod]
    public async Task ClearHasOwnBarrierAndKeepsArchives()
    {
        await using var writer = new LogPersistenceWriter(() => _root);
        var archive = Path.Combine(_root, "app-2000-01-01-preserved.log");
        writer.TryWrite(Entry("before-clear"));
        Assert.IsTrue((await writer.FlushAsync()).Succeeded);
        File.WriteAllText(archive, "archive-evidence");
        Assert.IsTrue((await writer.ClearAsync()).Succeeded);
        writer.TryWrite(Entry("after-clear"));
        Assert.IsTrue((await writer.FlushAsync()).Succeeded);
        var current = Directory.GetFiles(_root, "app-*.log").Single(path => path != archive);
        Assert.DoesNotContain("before-clear", ReadShared(current));
        StringAssert.Contains(ReadShared(current), "after-clear");
        Assert.AreEqual("archive-evidence", File.ReadAllText(archive));
    }

    [TestMethod]
    public async Task WriteFailureIsVisibleAndRecoveryDoesNotEraseGap()
    {
        var available = false;
        await using var writer = new LogPersistenceWriter(() => _root, open: path => available
            ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read) : throw new IOException("bare-fixture-secret"));
        writer.TryWrite(Entry("unconfirmed"));
        var failed = await writer.FlushAsync();
        Assert.AreEqual(LogPersistenceStatus.Unavailable, failed.Status);
        Assert.IsTrue(failed.Health.HasPersistenceGap);
        Assert.DoesNotContain("bare-fixture-secret", failed.Health.LastWriteFailure!);
        available = true;
        Assert.AreEqual(LogPersistenceStatus.KnownLoss, (await writer.FlushAsync()).Status);
        writer.TryWrite(Entry("recovered"));
        var recovered = await writer.FlushAsync();
        Assert.AreEqual(LogWriterState.Healthy, recovered.Health.WriterState);
        Assert.AreEqual(0, recovered.Health.ConsecutiveFailures);
        Assert.IsGreaterThan(0L, recovered.Health.FailedWriteAttempts);
        StringAssert.Contains(ReadLogs(), "recovered");
        Assert.DoesNotContain("unconfirmed", ReadLogs());
    }

    [TestMethod]
    public async Task BrokenSubscriberDoesNotHideEntriesOrPreventPersistence()
    {
        await using var writer = new LogPersistenceWriter(() => _root);
        var entry = Entry("published");
        var received = false;
        Action<LogEntry> subscribers = _ => throw new InvalidOperationException("subscriber failure");
        subscribers += _ => received = true;
        writer.TryWrite(entry);
        LogPersistenceWriter.Publish(subscribers, entry);
        Assert.IsTrue((await writer.FlushAsync()).Succeeded);
        Assert.IsTrue(received);
        StringAssert.Contains(ReadLogs(), "published");
    }

    [TestMethod]
    public async Task ClockRollbackDoesNotExtendFailureCooldown()
    {
        var clock = DateTimeOffset.UtcNow;
        var available = false;
        await using var writer = new LogPersistenceWriter(() => _root, () => clock, path => available
            ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read) : throw new IOException("unavailable"));
        writer.TryWrite(Entry("unconfirmed")); await writer.FlushAsync();
        clock = clock.AddDays(-1); available = true;
        await Task.Delay(300); // The two failed attempts have a 200 ms monotonic cooldown.
        writer.TryWrite(Entry("after-clock-rollback"));
        await writer.FlushAsync();
        StringAssert.Contains(ReadLogs(), "after-clock-rollback");
    }

    [TestMethod]
    public async Task DisabledIsNotPersistenceSuccess()
    {
        await using var writer = new LogPersistenceWriter(() => _root);
        writer.ApplyOptions(new() { EnableFileLogging = false });
        writer.TryWrite(Entry("memory-only"));
        var result = await writer.FlushAsync();
        Assert.AreEqual(LogPersistenceStatus.Disabled, result.Status);
        Assert.IsFalse(result.Succeeded);
        Assert.IsEmpty(Directory.GetFiles(_root));
    }

    [TestMethod]
    public async Task DisableStillAppliesWhenClosingFailedWriter()
    {
        await using var writer = new LogPersistenceWriter(() => _root, open: _ => new FailingFlushStream());
        writer.TryWrite(Entry("unconfirmed")); await writer.FlushAsync();
        writer.ApplyOptions(new() { EnableFileLogging = false });
        Assert.AreEqual(LogPersistenceStatus.Disabled, (await writer.FlushAsync()).Status);
        Assert.IsTrue(writer.Health.HasPersistenceGap);
    }

    [TestMethod]
    public async Task StopDeadlineDoesNotDisposeAWriterStillOpening()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new LogPersistenceWriter(() => _root, open: _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new MemoryStream();
        });
        writer.TryWrite(Entry("accepted"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await writer.StopWithinAsync(TimeSpan.FromMilliseconds(50));
            Assert.AreEqual(LogPersistenceStatus.TimedOut, result.Status);
            Assert.AreEqual(LogLifecycleState.Stopping, result.Health.LifecycleState);
        }
        finally { release.Set(); }
        Assert.IsTrue((await writer.StopAsync()).Succeeded);
    }

    [TestMethod]
    public async Task ConcurrentStopAndFlushCompleteAndLateEntriesAreRejected()
    {
        var writer = new LogPersistenceWriter(() => _root);
        writer.TryWrite(Entry("accepted"));
        var flush = writer.FlushAsync();
        var stop = writer.StopAsync();
        Assert.AreSame(stop, writer.StopAsync());
        await Task.WhenAll(flush, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(writer.TryWrite(Entry("late")));
        Assert.AreEqual(1, writer.Health.RejectedAfterStopCount);
        Assert.AreSame(stop, writer.FlushAsync());
        Assert.AreEqual(LogPersistenceStatus.Rejected, (await writer.ClearAsync()).Status);
        Assert.AreEqual(LogLifecycleState.Stopped, writer.Health.LifecycleState);
        StringAssert.Contains(ReadLogs(), "accepted");
        Assert.DoesNotContain("late", ReadLogs());
    }

    [TestMethod]
    public async Task PartialWriteIsNotReplayedDuringFailureCleanup()
    {
        var streams = new List<PartialWriteStream>();
        await using var writer = new LogPersistenceWriter(() => _root, open: _ => { var stream = new PartialWriteStream(); streams.Add(stream); return stream; });
        writer.TryWrite(Entry("unconfirmed"));
        Assert.AreEqual(LogPersistenceStatus.Unavailable, (await writer.FlushAsync()).Status);
        Assert.AreEqual(1, streams[0].WriteAttempts);
        Assert.IsTrue(writer.Health.HasPersistenceGap);
    }

    [TestMethod]
    public async Task FlushAndCloseFailuresCannotProduceSuccess()
    {
        await using var writer = new LogPersistenceWriter(() => _root, open: _ => new FailingFlushStream());
        writer.TryWrite(Entry("buffered"));
        Assert.AreEqual(LogPersistenceStatus.Unavailable, (await writer.FlushAsync()).Status);
        Assert.IsFalse((await writer.StopAsync()).Succeeded);
        Assert.IsTrue(writer.Health.HasPersistenceGap);
    }

    [TestMethod]
    public async Task ClearFailureHasIndependentFailureResult()
    {
        var invalid = Path.Combine(_root, "not-a-directory"); File.WriteAllText(invalid, "evidence");
        await using var writer = new LogPersistenceWriter(() => invalid);
        Assert.AreEqual(LogPersistenceStatus.Unavailable, (await writer.ClearAsync()).Status);
        Assert.AreEqual("evidence", File.ReadAllText(invalid));
    }

    [TestMethod]
    public async Task CrossDayAndClockRollbackUseWriteDateWithoutReordering()
    {
        var clock = new DateTimeOffset(2026, 11, 8, 23, 59, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 8)));
        await using var writer = new LogPersistenceWriter(() => _root, () => clock);
        writer.TryWrite(Entry("first")); await writer.FlushAsync();
        clock = clock.AddMinutes(2);
        writer.TryWrite(Entry("second")); await writer.FlushAsync();
        clock = clock.AddMinutes(-1);
        writer.TryWrite(Entry("third")); await writer.FlushAsync();
        Assert.HasCount(2, Directory.GetFiles(_root, "app-*.log"));
        var next = ReadShared(Path.Combine(_root, "app-2026-11-09.log"));
        Assert.IsLessThan(next.IndexOf("third", StringComparison.Ordinal), next.IndexOf("second", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RetentionFailureDoesNotPreventWriting()
    {
        var expired = Path.Combine(_root, "app-2000-01-01.log"); File.WriteAllText(expired, "old");
        using var held = new FileStream(expired, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var writer = new LogPersistenceWriter(() => _root);
        writer.TryWrite(Entry("still-writable"));
        var result = await writer.FlushAsync();
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Health.LastMaintenanceFailure);
        StringAssert.Contains(ReadLogs(), "still-writable");
    }

    [TestMethod]
    public void FormattingRetainsContextAndMarksRejectedEntries()
    {
        var entry = Entry("token=fixture-secret");
        entry.OperationId = "operation";
        entry.Context = new("operation", "Restore", TransactionId: "transaction");
        entry.PersistenceAccepted = false;
        var formatted = LogPersistenceWriter.FormatEntry(entry);
        StringAssert.Contains(formatted, "[transaction:transaction]");
        StringAssert.Contains(formatted, "[persistence:rejected]");
        Assert.DoesNotContain("fixture-secret", formatted);
    }

    private sealed class FailingFlushStream : MemoryStream
    {
        public override void Flush() => throw new IOException("sensitive-flush-error");
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw new IOException("sensitive-close-error"); }
    }
    private sealed class PartialWriteStream : Stream
    {
        private readonly MemoryStream _stored = new();
        internal int WriteAttempts;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _stored.Length;
        public override long Position { get => _stored.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer)
        { WriteAttempts++; _stored.Write(buffer[..Math.Min(2, buffer.Length)]); throw new IOException("partial write"); }
        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing) { if (disposing) _stored.Dispose(); base.Dispose(disposing); }
    }
}
