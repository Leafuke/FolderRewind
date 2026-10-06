using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

public sealed record BackupPhaseTiming(string Code, TimeSpan Elapsed);
public sealed record BackupTimingSummary(string ConfigId, DateTimeOffset StartedAtUtc, TimeSpan Total, IReadOnlyList<BackupPhaseTiming> Phases);

public static class BackupTimingService
{
    private static readonly object Sync = new();
    private static readonly Queue<BackupTimingSummary> Recent = new();
    public static IReadOnlyList<BackupTimingSummary> GetRecent(string configId)
    { lock (Sync) return Recent.Where(r => r.ConfigId == configId).ToArray(); }
    public static BackupTimingScope Begin(string configId, TimeProvider? clock = null) => new(configId, clock ?? TimeProvider.System, summary =>
    {
        lock (Sync) { Recent.Enqueue(summary); while (Recent.Count > OnboardingOperationBudgets.RecentDiagnostics) Recent.Dequeue(); }
    });
}

// 串行粗粒度阶段使用单调时钟；协调、扫描与压缩合并呈现，不虚构硬件归因。
public sealed class BackupTimingScope : IDisposable
{
    private readonly string _config;
    private readonly TimeProvider _clock;
    private readonly Action<BackupTimingSummary> _publish;
    private readonly long _start;
    private long _phaseStart;
    private readonly DateTimeOffset _date;
    private string _phase = "queue-initialize";
    private readonly List<BackupPhaseTiming> _phases = [];
    private bool _disposed;
    public BackupTimingScope(string configId, TimeProvider clock, Action<BackupTimingSummary> publish)
    { _config = configId; _clock = clock; _publish = publish; _start = _phaseStart = clock.GetTimestamp(); _date = clock.GetUtcNow(); }
    public void MarkPhase(string code)
    {
        if (_disposed) return;
        var now = _clock.GetTimestamp();
        _phases.Add(new(_phase, _clock.GetElapsedTime(_phaseStart, now))); _phase = code; _phaseStart = now;
    }
    public void Dispose()
    {
        if (_disposed) return;
        var now = _clock.GetTimestamp(); _phases.Add(new(_phase, _clock.GetElapsedTime(_phaseStart, now))); _disposed = true;
        _publish(new(_config, _date, _clock.GetElapsedTime(_start, now), _phases.ToArray()));
    }
}
