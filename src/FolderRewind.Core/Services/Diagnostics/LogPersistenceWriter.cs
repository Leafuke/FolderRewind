using FolderRewind.Models;
using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace FolderRewind.Services;

/// <summary>Instance-owned production file writer. Only its single consumer touches files.</summary>
internal sealed class LogPersistenceWriter : IAsyncDisposable
{
    private sealed record Work(LogEntry? Entry = null, LogOptions? Options = null, bool Clear = false,
        TaskCompletionSource<LogPersistenceResult>? Completion = null);
    private readonly object _sync = new();
    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly TaskCompletionSource<LogPersistenceResult> _stopped = NewCompletion();
    private readonly Func<string> _directory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, Stream> _open;
    private readonly Task _worker;
    private LogOptions _options = new();
    private LogHealthSnapshot _health = new(LogLifecycleState.Running, LogWriterState.Healthy, null, null, null, 0, 0, 0, false);
    private StreamWriter? _writer;
    private Stream? _stream;
    private string? _path;
    private string? _trimDate;
    private long _bytes;
    private long _lastFailureTimestamp;
    private TimeSpan _retryDelay;

    internal LogPersistenceWriter(Func<string> directory, Func<DateTimeOffset>? utcNow = null, Func<string, Stream>? open = null)
    {
        _directory = directory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _open = open ?? (path => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024));
        if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(ProcessAsync);
        else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(ProcessAsync); }
    }

    internal LogHealthSnapshot Health { get { lock (_sync) return _health; } }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    private static TaskCompletionSource<LogPersistenceResult> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool TryWrite(LogEntry entry)
    {
        lock (_sync)
        {
            if (_health.LifecycleState == LogLifecycleState.Running && _channel.Writer.TryWrite(new(Entry: entry))) return true;
            _health = _health with { RejectedAfterStopCount = _health.RejectedAfterStopCount + 1 };
            return false;
        }
    }

    internal void ApplyOptions(LogOptions options)
    {
        lock (_sync)
            if (_health.LifecycleState == LogLifecycleState.Running) _channel.Writer.TryWrite(new(Options: Normalize(options)));
    }

    internal Task<LogPersistenceResult> FlushAsync() => ControlAsync(false);
    internal Task<LogPersistenceResult> ClearAsync() => ControlAsync(true);
    private Task<LogPersistenceResult> ControlAsync(bool clear)
    {
        lock (_sync)
        {
            if (_health.LifecycleState != LogLifecycleState.Running)
                return clear ? Task.FromResult(new LogPersistenceResult(LogPersistenceStatus.Rejected, _health)) : _stopped.Task;
            var completion = NewCompletion();
            _channel.Writer.TryWrite(new(Clear: clear, Completion: completion));
            return completion.Task;
        }
    }

    internal Task<LogPersistenceResult> StopAsync()
    {
        lock (_sync)
        {
            if (_health.LifecycleState == LogLifecycleState.Running)
            {
                _health = _health with { LifecycleState = LogLifecycleState.Stopping };
                _channel.Writer.TryComplete();
            }
            return _stopped.Task;
        }
    }

    internal async Task<LogPersistenceResult> StopWithinAsync(TimeSpan budget)
    {
        try { return await StopAsync().WaitAsync(budget).ConfigureAwait(false); }
        catch (TimeoutException) { return new(LogPersistenceStatus.TimedOut, Health); }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var count = 0;
                while (count++ < 100 && _channel.Reader.TryRead(out var work))
                {
                    var before = Health.FailedWriteAttempts;
                    try
                    {
                        if (work.Options is { } options)
                        {
                            try { CloseWriter(); } catch (Exception error) { RecordFailure(error); ResetWriter(); }
                            _options = options;
                            lock (_sync) _health = _health with { WriterState = !options.EnableFileLogging ? LogWriterState.Disabled
                                : _health.ConsecutiveFailures > 0 ? LogWriterState.Degraded : LogWriterState.Healthy };
                            TrimFiles();
                        }
                        if (work.Clear)
                        {
                            CloseWriter();
                            Directory.CreateDirectory(_directory());
                            File.WriteAllText(CurrentPath(), string.Empty);
                        }
                        if (work.Entry is { } entry) Append(entry);
                        if (work.Completion is not null) FlushWriter(force: true);
                    }
                    catch (Exception error) { RecordFailure(error); ResetWriter(); }
                    if (work.Completion is { } completion)
                        completion.TrySetResult(Result(work.Clear && Health.FailedWriteAttempts == before));
                }
                try { FlushWriter(); }
                catch (Exception error) { RecordFailure(error); ResetWriter(); }
            }
            try { FlushWriter(force: true); CloseWriter(); }
            catch (Exception error) { RecordFailure(error); ResetWriter(); }
            lock (_sync) _health = _health with { LifecycleState = LogLifecycleState.Stopped };
        }
        catch (Exception error)
        {
            RecordFailure(error);
            ResetWriter();
            lock (_sync) _health = _health with { LifecycleState = LogLifecycleState.Faulted, WriterState = LogWriterState.Faulted };
        }
        finally
        {
            _channel.Writer.TryComplete();
            var result = Result();
            while (_channel.Reader.TryRead(out var pending)) pending.Completion?.TrySetResult(result);
            _stopped.TrySetResult(result);
        }
    }

    private string CurrentPath() => Path.Combine(_directory(), "app-" + _utcNow().ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    private void EnsureWriter()
    {
        var path = CurrentPath();
        if (_path != path || _bytes >= Math.Max(1024, _options.MaxFileSizeKb) * 1024L) CloseWriter();
        if (_writer is not null) return;
        Directory.CreateDirectory(_directory());
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length >= Math.Max(1024, _options.MaxFileSizeKb) * 1024L)
                File.Move(path, Path.Combine(_directory(), Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".log"));
        }
        catch (Exception error) { RecordMaintenance(error); }
        var date = _utcNow().ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_trimDate != date) { _trimDate = date; TrimFiles(); }
        var stream = _open(path);
        try { _bytes = stream.CanSeek ? stream.Length : 0; _writer = new(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true); _stream = stream; _path = path; }
        catch { stream.Dispose(); throw; }
    }

    private void Append(LogEntry entry)
    {
        if (!_options.EnableFileLogging) return;
        if (_retryDelay > TimeSpan.Zero && Stopwatch.GetElapsedTime(_lastFailureTimestamp) < _retryDelay)
        {
            lock (_sync) _health = _health with { HasPersistenceGap = true };
            return;
        }
        EnsureWriter();
        var line = FormatEntry(entry);
        _writer!.WriteLine(line);
        _bytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
    }

    private void FlushWriter(bool force = false)
    {
        if (!_options.EnableFileLogging) return;
        if (force) EnsureWriter();
        if (_writer is null) return;
        _writer.Flush();
        _retryDelay = TimeSpan.Zero;
        lock (_sync) _health = _health with { WriterState = LogWriterState.Healthy, ConsecutiveFailures = 0, LastSuccessfulFlushUtc = _utcNow() };
    }

    private void CloseWriter()
    {
        var writer = _writer; var stream = _stream; _writer = null; _stream = null; _path = null; _bytes = 0;
        try { writer?.Dispose(); } finally { stream?.Dispose(); }
    }
    private void ResetWriter()
    {
        // Disposing StreamWriter would retry its unconfirmed buffer. Release only the underlying
        // stream after a write/flush fault; those records remain explicitly unconfirmed.
        var stream = _stream; _writer = null; _stream = null; _path = null; _bytes = 0;
        try { stream?.Dispose(); } catch (Exception error) { RecordFailure(error); }
    }
    private void RecordFailure(Exception error)
    {
        lock (_sync)
        {
            var consecutive = Math.Min(30, _health.ConsecutiveFailures + 1);
            _health = _health with { WriterState = LogWriterState.Degraded, LastWriteFailure = DiagnosticPrivacy.ExceptionMetadata(error),
                FailedWriteAttempts = _health.FailedWriteAttempts + 1, ConsecutiveFailures = consecutive, HasPersistenceGap = true };
            _lastFailureTimestamp = Stopwatch.GetTimestamp();
            _retryDelay = TimeSpan.FromMilliseconds(Math.Min(5000, 100 * Math.Pow(2, consecutive - 1)));
        }
    }
    private void RecordMaintenance(Exception error)
    { lock (_sync) _health = _health with { LastMaintenanceFailure = DiagnosticPrivacy.ExceptionMetadata(error) }; }

    private LogPersistenceResult Result(bool clearSucceeded = false)
    {
        var health = Health;
        var status = health.LifecycleState == LogLifecycleState.Faulted ? LogPersistenceStatus.Faulted
            : health.WriterState == LogWriterState.Degraded ? LogPersistenceStatus.Unavailable
            : clearSucceeded ? LogPersistenceStatus.Persisted
            : health.WriterState == LogWriterState.Disabled ? LogPersistenceStatus.Disabled
            : health.HasPersistenceGap ? LogPersistenceStatus.KnownLoss : LogPersistenceStatus.Persisted;
        return new(status, health);
    }

    private void TrimFiles()
    {
        try
        {
            if (!Directory.Exists(_directory())) return;
            var threshold = _utcNow().ToLocalTime().Date.AddDays(-_options.RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory(), "app-*.log"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var expired = name.Length >= 14 && DateTime.TryParseExact(name.Substring(4, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) ? date < threshold : File.GetLastWriteTime(file) < threshold;
                    if (expired && file != _path) File.Delete(file);
                }
                catch (Exception error) { RecordMaintenance(error); }
            }
        }
        catch (Exception error) { RecordMaintenance(error); }
    }

    internal static LogOptions Normalize(LogOptions options) => new()
    {
        EnableFileLogging = options.EnableFileLogging, MaxEntries = Math.Max(500, options.MaxEntries),
        MaxFileSizeKb = Math.Clamp(options.MaxFileSizeKb, 512, 1024 * 50), RetentionDays = Math.Clamp(options.RetentionDays, 1, 60)
    };

    internal static void Publish(Action<LogEntry>? subscribers, LogEntry entry)
    {
        foreach (var subscriber in subscribers?.GetInvocationList() ?? [])
        {
            try { ((Action<LogEntry>)subscriber)(entry); }
            catch { /* Subscriber failure must not affect persistence or another subscriber. */ }
        }
    }

    internal static string FormatEntry(LogEntry entry)
    {
        var fields = new StringBuilder();
        void Add(string name, object? value) { if (value is not null) fields.Append('[').Append(name).Append(':').Append(DiagnosticPrivacy.Text(value.ToString()).Replace('\r', ' ').Replace('\n', ' ')).Append("] "); }
        Add("operation", entry.OperationId);
        if (entry.Context is { } c)
        {
            Add("type", c.OperationType); Add("config", c.ConfigId); Add("source", c.SourceId); Add("run", c.RunId);
            Add("transaction", c.TransactionId); Add("merge-session", c.MergeSessionId); Add("plan-revision", c.PlanRevision); Add("provider", c.ProviderId);
            Add("job", c.JobId); Add("job-attempt", c.JobAttempt); Add("command-attempt", c.CommandAttempt);
            Add("plugin", c.PluginId); Add("capability", c.Capability); Add("coordination", c.CoordinationOperationId); Add("parent", c.ParentOperationId);
        }
        if (!entry.PersistenceAccepted) Add("persistence", "rejected");
        var source = string.IsNullOrWhiteSpace(entry.Source) ? "" : "[" + DiagnosticPrivacy.Text(entry.Source) + "] ";
        var error = string.IsNullOrWhiteSpace(entry.Exception) ? "" : " | " + DiagnosticPrivacy.Text(entry.Exception);
        return FormattableString.Invariant($"[{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}] [{entry.Level.ToString().ToUpperInvariant()}] {source}{fields}{DiagnosticPrivacy.Text(entry.Message)}{error}");
    }
}
