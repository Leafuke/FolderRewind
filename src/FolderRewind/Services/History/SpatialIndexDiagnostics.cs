using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
using System.IO;
#endif

namespace FolderRewind.Services;

internal sealed record SpatialIndexPhaseSample(string Name, double Milliseconds, int Calls, int Commands);
internal sealed record SpatialIndexOperationSample(long Id, string Kind, double WaitMilliseconds,
    double HoldMilliseconds, int Commands, long BeginVersion, long EndVersion, bool Canceled,
    IReadOnlyList<SpatialIndexPhaseSample> Phases, IReadOnlyDictionary<string, string> Context,
    long StartedTimestamp, long AcquiredTimestamp, long CompletedTimestamp, double? CancellationMilliseconds);
internal sealed record SpatialIndexUiSample(string Kind, long Generation, long IndexVersion, double? QueueMilliseconds,
    double ApplyMilliseconds, bool Published);

// Explicitly supplied by a measurement session. Normal pages do not create or retain a collector.
internal sealed class SpatialIndexDiagnostics
{
    private readonly ConcurrentQueue<SpatialIndexOperationSample> _operations = new();
    private readonly ConcurrentQueue<SpatialIndexUiSample> _ui = new();
    private long _nextId;
    private int _activeOperations, _peakOperations;
    internal IReadOnlyList<SpatialIndexOperationSample> Operations => _operations.ToArray();
    internal IReadOnlyList<SpatialIndexUiSample> Ui => _ui.ToArray();
    internal int PeakOperations => Volatile.Read(ref _peakOperations);
    internal Operation Begin(string kind, CancellationToken token)
    {
        var active = Interlocked.Increment(ref _activeOperations);
        int peak;
        do { peak = Volatile.Read(ref _peakOperations); }
        while (active > peak && Interlocked.CompareExchange(ref _peakOperations, active, peak) != peak);
        return new(this, Interlocked.Increment(ref _nextId), kind, token);
    }
    internal void RecordUi(long generation, long version, long queuedAt, long startedAt, bool published)
        => _ui.Enqueue(new("rows", generation, version, Stopwatch.GetElapsedTime(queuedAt, startedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, published));
    internal void RecordOverlayUi(long generation, long version, long startedAt, bool published)
        => _ui.Enqueue(new("overlay", generation, version, null,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, published));

    internal sealed class Operation
    {
        private readonly SpatialIndexDiagnostics _owner;
        private readonly long _id;
        private readonly string _kind;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly CancellationTokenRegistration _cancellation;
        private long _canceledAt;
        private long _acquired, _version;
        private readonly Dictionary<string, (long Ticks, int Calls, int Commands)> _phases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _context = new(StringComparer.Ordinal);
        internal int Commands { get; set; }
        internal bool Canceled { get; set; }
        internal Operation(SpatialIndexDiagnostics owner, long id, string kind, CancellationToken token)
        {
            _owner = owner; _id = id; _kind = kind;
            _cancellation = token.UnsafeRegister(static state =>
            {
                var operation = (Operation)state!;
                Interlocked.CompareExchange(ref operation._canceledAt, Stopwatch.GetTimestamp(), 0);
            }, this);
        }
        internal void Acquired(long version) { _acquired = Stopwatch.GetTimestamp(); _version = version; }
        internal Phase Measure(string name) => new(this, name);
        internal void Context(string name, string value) => _context[name] = value;
        internal void Complete(long version, long releasedAt)
        {
            _cancellation.Dispose();
            Interlocked.Decrement(ref _owner._activeOperations);
            var canceledAt = Volatile.Read(ref _canceledAt);
            _owner._operations.Enqueue(new(_id, _kind,
                Stopwatch.GetElapsedTime(_started, _acquired == 0 ? releasedAt : _acquired).TotalMilliseconds,
                _acquired == 0 ? 0 : Stopwatch.GetElapsedTime(_acquired, releasedAt).TotalMilliseconds, Commands, _version, version, Canceled,
                _phases.Select(pair => new SpatialIndexPhaseSample(pair.Key,
                    pair.Value.Ticks * 1000d / Stopwatch.Frequency, pair.Value.Calls, pair.Value.Commands)).ToArray(),
                new Dictionary<string, string>(_context, StringComparer.Ordinal),
                _started, _acquired, releasedAt, canceledAt == 0 ? null :
                    Math.Max(0, Stopwatch.GetElapsedTime(canceledAt, releasedAt).TotalMilliseconds)));
        }
        private void Add(string name, long ticks, int commands)
        {
            _phases.TryGetValue(name, out var value);
            _phases[name] = (value.Ticks + ticks, value.Calls + 1, value.Commands + commands);
        }

        internal readonly struct Phase : IDisposable
        {
            private readonly Operation? _operation;
            private readonly string? _name;
            private readonly long _started;
            private readonly int _commands;
            internal Phase(Operation operation, string name)
            { _operation = operation; _name = name; _started = Stopwatch.GetTimestamp(); _commands = operation.Commands; }
            public void Dispose()
            {
                if (_operation is not null)
                    _operation.Add(_name!, Stopwatch.GetTimestamp() - _started, _operation.Commands - _commands);
            }
        }
    }
}

internal sealed partial class SpatialWorkspaceIndex
{
    private SpatialIndexDiagnostics.Operation? _currentOperation;
    private long _mutationVersion;
    internal SpatialIndexDiagnostics? Diagnostics => _diagnostics;
#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
    internal void SaveDiagnosticSnapshot(string destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (File.Exists(destination)) throw new IOException("The diagnostic index snapshot already exists.");
            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = destination, Pooling = false }.ToString());
            copy.Open();
            Db.BackupDatabase(copy);
        }
    }
#endif

    private GateLease EnterOperation(string kind, CancellationToken observedToken = default,
        IReadOnlyDictionary<string, string>? context = null)
    {
        // Only the outer operation is metered; nested Query/Count calls retain their reentrant gate.
        var operation = _diagnostics is not null && !Monitor.IsEntered(_gate) ? _diagnostics.Begin(kind, observedToken) : null;
        var entered = false;
        try
        {
            if (operation is not null && context is not null)
                foreach (var pair in context) operation.Context(pair.Key, pair.Value);
            if (!observedToken.CanBeCanceled || Monitor.IsEntered(_gate)) Monitor.Enter(_gate);
            else
            {
                observedToken.ThrowIfCancellationRequested();
                while (!Monitor.TryEnter(_gate, millisecondsTimeout: 10)) observedToken.ThrowIfCancellationRequested();
            }
            entered = true;
            operation?.Acquired(_mutationVersion);
            observedToken.ThrowIfCancellationRequested();
        }
        catch
        {
            var version = entered ? _mutationVersion : 0;
            if (entered) Monitor.Exit(_gate);
            if (operation is not null)
            {
                operation.Canceled = observedToken.IsCancellationRequested;
                operation.Complete(version, Stopwatch.GetTimestamp());
            }
            throw;
        }
        var previous = _currentOperation;
        if (operation is not null) _currentOperation = operation;
        return new(this, previous, operation, observedToken);
    }
    private SpatialIndexDiagnostics.Operation.Phase Measure(string name)
        => _currentOperation?.Measure(name) ?? default;
    private SqliteCommand Executed(SqliteCommand command)
    {
        // Counts command executions; a multi-statement schema/clear command counts as one.
        if (_currentOperation is { } operation) operation.Commands++;
        return command;
    }
    private readonly struct GateLease(SpatialWorkspaceIndex index, SpatialIndexDiagnostics.Operation? previous,
        SpatialIndexDiagnostics.Operation? operation, CancellationToken observedToken) : IDisposable
    {
        public void Dispose()
        {
            var releasedAt = operation is null ? 0 : Stopwatch.GetTimestamp();
            var version = index._mutationVersion;
            if (operation is not null && observedToken.IsCancellationRequested) operation.Canceled = true;
            index._currentOperation = previous;
            Monitor.Exit(index._gate);
            operation?.Complete(version, releasedAt);
        }
    }
}
