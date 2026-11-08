using FolderRewind.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;

namespace FolderRewind.Services;

/// <summary>Host-owned final result and explicit Core summary handoff; never infers a business outcome.</summary>
internal sealed class BusinessOperationDiagnostics : IDisposable
{
    private readonly Action<string, LogLevel> _publish;
    private readonly HistoryOperationDiagnostics _history;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _type;
    private readonly bool _internal;
    private readonly List<HistoryOperationDiagnostic> _summaries = new();
    private string _outcome = "Interrupted", _detail = "";
    private LogLevel _level = LogLevel.Warning;
    private bool _completed, _disposed;

    internal BusinessOperationDiagnostics(string type, bool internalOperation = false, Action<string, LogLevel>? publish = null)
    {
        _type = type; _internal = internalOperation;
        _publish = publish ?? ((text, level) => LogService.Log(text, level, type));
        _history = HistoryOperationDiagnostics.Begin(type, summary => { lock (_summaries) _summaries.Add(summary); });
        Publish("event=operation.started; type=" + type, LogLevel.Info);
    }
    internal void Complete(string outcome, LogLevel level = LogLevel.Info, string detail = "")
    {
        if (_completed) return;
        _completed = true; _outcome = outcome; _level = level; _detail = detail;
    }
    internal void Failure(Exception error, bool canceled = false)
    {
        Complete(canceled ? "Canceled" : "Failed", canceled ? LogLevel.Info : LogLevel.Error);
        if (!canceled) Publish("event=operation.error; " + DiagnosticPrivacy.ExceptionMetadata(error), LogLevel.Error);
    }
    private void Publish(string text, LogLevel level)
    { try { _publish(text, level); } catch { /* Logging cannot change business results. */ } }
    private void PublishHistory(HistoryOperationDiagnostic summary)
    {
        var counts = string.Join(",", summary.Counters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
        var phases = string.Join(",", summary.InclusivePhases.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)));
        var observations = string.Join("; ", summary.Observations.Where(p => p.Key is "transaction" or "stage" or "failure-stage" or "mutation-strategy")
            .Select(p => p.Key + "=" + DiagnosticPrivacy.Text(p.Value)));
        Publish(FormattableString.Invariant($"event=history.diagnostics; type={summary.Operation}; elapsedMs={summary.Elapsed.TotalMilliseconds:F1}; counters={counts}; inclusivePhaseMs={phases}; {observations}"), _level);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _history.Dispose(); } catch (Exception error) { Publish(DiagnosticPrivacy.ExceptionMetadata(error), LogLevel.Warning); }
        HistoryOperationDiagnostic[] summaries;
        lock (_summaries) summaries = _summaries.ToArray();
        foreach (var summary in summaries) PublishHistory(summary);
        Publish(FormattableString.Invariant($"event={(_internal ? "operation.subresult" : "operation.completed")}; type={_type}; outcome={_outcome}; elapsedMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F1}; {_detail}"), _level);
    }
}
