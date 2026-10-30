using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class OnboardingPerformanceTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(12)]
    public void PerformancePresetsDeriveWithoutChangingOtherPolicy(int processors)
    {
        for (int index = 0; index < 3; index++)
        {
            var values = BackupPerformancePolicy.Get(index, processors, new(0, false));
            Assert.AreEqual(index, BackupPerformancePolicy.Derive(values, processors, index));
            Assert.IsTrue(values.CpuThreads >= 0 && values.CpuThreads <= processors);
        }
        Assert.AreEqual(3, BackupPerformancePolicy.Derive(new(processors, false), processors));
    }

    [TestMethod]
    public void TimingUsesMonotonicClockAndSerialStagesWithoutAddingOverlappingTime()
    {
        var clock = new ManualClock(); BackupTimingSummary? summary = null;
        using (var timing = new BackupTimingScope("config", clock, result => summary = result))
        {
            clock.Advance(TimeSpan.FromSeconds(2)); timing.MarkPhase("capture-coordination-scan-archive");
            clock.Advance(TimeSpan.FromSeconds(3)); timing.MarkPhase("history-commit");
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.IsNotNull(summary);
        Assert.AreEqual(TimeSpan.FromSeconds(6), summary.Total);
        Assert.AreEqual(summary.Total, TimeSpan.FromTicks(summary.Phases.Sum(p => p.Elapsed.Ticks)));
    }

    [TestMethod]
    public void OnlyRecentTwentyTimingRecordsAreRetained()
    {
        var id = Guid.NewGuid().ToString();
        for (int index = 0; index < 25; index++) BackupTimingService.Begin(id).Dispose();
        Assert.HasCount(20, BackupTimingService.GetRecent(id));
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks);
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
