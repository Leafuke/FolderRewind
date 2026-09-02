using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class AsyncPeriodicLoopTests
{
    [TestMethod]
    public async Task FakeTime_DrivesScheduleAndConditionPeriods()
    {
        var time = new ManualTimeProvider();
        var scheduleCount = 0;
        var conditionCount = 0;
        await using var schedule = new AsyncPeriodicLoop(
            time,
            TimeSpan.FromSeconds(60),
            runImmediately: false,
            _ =>
            {
                Interlocked.Increment(ref scheduleCount);
                return Task.CompletedTask;
            },
            _ => { });
        await using var condition = new AsyncPeriodicLoop(
            time,
            TimeSpan.FromSeconds(10),
            runImmediately: false,
            _ =>
            {
                Interlocked.Increment(ref conditionCount);
                return Task.CompletedTask;
            },
            _ => { });

        await schedule.StartAsync();
        await condition.StartAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => Volatile.Read(ref conditionCount) == 1);
        Assert.AreEqual(0, Volatile.Read(ref scheduleCount));

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => Volatile.Read(ref conditionCount) == i + 2);
        }

        await WaitUntilAsync(() => Volatile.Read(ref scheduleCount) == 1);
        Assert.AreEqual(6, Volatile.Read(ref conditionCount));
    }

    [TestMethod]
    public async Task SlowIteration_DoesNotOverlap()
    {
        var time = new ManualTimeProvider();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var iterationCount = 0;
        await using var loop = new AsyncPeriodicLoop(
            time,
            TimeSpan.FromSeconds(10),
            runImmediately: true,
            async _ =>
            {
                var currentActive = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, currentActive);
                var currentIteration = Interlocked.Increment(ref iterationCount);
                if (currentIteration == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                Interlocked.Decrement(ref active);
            },
            _ => { });

        await loop.StartAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, Volatile.Read(ref iterationCount));

        releaseFirst.TrySetResult();
        await WaitUntilAsync(() => Volatile.Read(ref iterationCount) >= 2);
        Assert.AreEqual(1, Volatile.Read(ref maximumActive));
    }

    [TestMethod]
    public async Task IterationFailure_IsReportedAndNextTickStillRuns()
    {
        var time = new ManualTimeProvider();
        var attempts = 0;
        var failures = 0;
        await using var loop = new AsyncPeriodicLoop(
            time,
            TimeSpan.FromSeconds(10),
            runImmediately: false,
            _ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("first round failed"))
                : Task.CompletedTask,
            _ => Interlocked.Increment(ref failures));

        await loop.StartAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => Volatile.Read(ref failures) == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 2);

        Assert.AreEqual(1, Volatile.Read(ref failures));
    }

    [TestMethod]
    public async Task StopAsync_IsIdempotentAndPreventsFutureIterations()
    {
        var time = new ManualTimeProvider();
        var count = 0;
        await using var loop = new AsyncPeriodicLoop(
            time,
            TimeSpan.FromSeconds(10),
            runImmediately: false,
            _ =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            },
            _ => { });

        await loop.StartAsync();
        await loop.StartAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => Volatile.Read(ref count) == 1);
        await loop.StopAsync();
        await loop.StopAsync();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.AreEqual(1, Volatile.Read(ref count));
        Assert.IsFalse(loop.IsRunning);
    }

    [TestMethod]
    public async Task StopAsync_WaitsForActiveIterationToFinish()
    {
        var time = new ManualTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var loop = new AsyncPeriodicLoop(
            time,
            TimeSpan.FromSeconds(10),
            runImmediately: true,
            async _ =>
            {
                Interlocked.Increment(ref count);
                started.TrySetResult();
                await release.Task;
            },
            _ => { });

        await loop.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopTask = loop.StopAsync();
        Assert.IsFalse(stopTask.IsCompleted);

        release.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.AreEqual(1, Volatile.Read(ref count));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
                Assert.Fail("Timed out waiting for the periodic loop.");
            await Task.Yield();
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_sync) return _utcNow.UtcTicks;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            DateTimeOffset target;
            lock (_sync) target = _utcNow + amount;

            while (true)
            {
                ManualTimer[] due;
                lock (_sync)
                {
                    var nextDue = _timers
                        .Where(timer => timer.NextDueUtc is not null && timer.NextDueUtc <= target)
                        .Select(timer => timer.NextDueUtc)
                        .Min();
                    if (nextDue is null)
                    {
                        _utcNow = target;
                        return;
                    }

                    _utcNow = nextDue.Value;
                    due = _timers.Where(timer => timer.NextDueUtc == nextDue).ToArray();
                    foreach (var timer in due) timer.AdvanceDueTime();
                }

                foreach (var timer in due) timer.Invoke();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync) _timers.Remove(timer);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                NextDueUtc = ToDueTime(owner._utcNow, dueTime);
            }

            public DateTimeOffset? NextDueUtc { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._sync)
                {
                    if (_disposed) return false;
                    _period = period;
                    NextDueUtc = ToDueTime(_owner._utcNow, dueTime);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_owner._sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    NextDueUtc = null;
                }
                _owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void AdvanceDueTime()
            {
                if (_disposed || NextDueUtc is null) return;
                NextDueUtc = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : NextDueUtc.Value + _period;
            }

            public void Invoke()
            {
                if (!_disposed) _callback(_state);
            }

            private static DateTimeOffset? ToDueTime(DateTimeOffset now, TimeSpan dueTime)
                => dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
        }
    }
}
