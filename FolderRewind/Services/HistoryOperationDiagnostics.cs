using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

public sealed record HistoryOperationDiagnostic(string Operation, DateTimeOffset StartedAtUtc,
    TimeSpan Elapsed, IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, TimeSpan> InclusivePhases, IReadOnlyDictionary<string, string> Observations);

/// <summary>操作内诊断；嵌套阶段为 inclusive，不能相加为总耗时。只统计已有 I/O，不额外扫描内容。</summary>
public sealed class HistoryOperationDiagnostics : IDisposable
{
    private static readonly AsyncLocal<HistoryOperationDiagnostics?> Ambient = new();
    private static readonly Queue<HistoryOperationDiagnostic> Recent = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _phases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _observations = new(StringComparer.Ordinal);
    private readonly HistoryOperationDiagnostics? _previous;
    private readonly string _operation;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _date = DateTimeOffset.UtcNow;
    private bool _disposed;
    public static HistoryOperationDiagnostics? Current => Ambient.Value;
    public static HistoryOperationDiagnostics Begin(string operation) => new(operation);
    private HistoryOperationDiagnostics(string operation)
    { _operation = operation; _previous = Ambient.Value; Ambient.Value = this; }
    public void Count(string code, long value = 1)
    { lock (_sync) _counters[code] = checked(_counters.GetValueOrDefault(code) + value); }
    public void Observe(string code, string value)
    { lock (_sync) _observations[code] = value; }
    public IDisposable Measure(string code) => new Phase(this, code);
    public static IReadOnlyList<HistoryOperationDiagnostic> GetRecent()
    { lock (Recent) return Recent.ToArray(); }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var result = new HistoryOperationDiagnostic(_operation, _date, Stopwatch.GetElapsedTime(_started),
                new Dictionary<string, long>(_counters), new Dictionary<string, TimeSpan>(_phases),
                new Dictionary<string, string>(_observations));
            lock (Recent) { Recent.Enqueue(result); while (Recent.Count > 32) Recent.Dequeue(); }
        }
        Ambient.Value = _previous;
    }
    private sealed class Phase(HistoryOperationDiagnostics owner, string code) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private int _finished;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            lock (owner._sync)
                owner._phases[code] = owner._phases.GetValueOrDefault(code) + Stopwatch.GetElapsedTime(_start);
        }
    }
}
