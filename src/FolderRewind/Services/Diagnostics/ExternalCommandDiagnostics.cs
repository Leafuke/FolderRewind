using FolderRewind.Models;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

/// <summary>Records process metadata only; arguments, environment and console output never enter this projection.</summary>
internal sealed class ExternalCommandDiagnostics : IDisposable
{
    private readonly NotificationOperationScope? _scope;
    private readonly CancellationToken _userCancellation;
    private readonly string _category;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Action<string, LogLevel> _publish;
    private int? _exitCode;
    private string _outcome = "StartFailed";
    private bool _disposed;

    internal ExternalCommandDiagnostics(ProcessStartInfo start, CancellationToken userCancellation, Action<string, LogLevel>? publish = null)
    {
        _userCancellation = userCancellation;
        _publish = publish ?? ((message, level) => LogService.Log(message, level, "CloudProcess"));
        _category = CloudCommandSecurity.DetectOperationCategory(start.ArgumentList.FirstOrDefault() ?? start.Arguments);
        if (NotificationOperationScope.Current is { } parent)
        {
            var attempt = parent.NextCommandAttempt();
            _scope = NotificationOperationScope.EnterChild(c => c with { CommandAttempt = attempt });
        }
        else _scope = new(new(NotificationTargetKind.CloudProject), context: new("", "CloudCommand", CommandAttempt: 1));
    }

    internal void Complete(int? exitCode, bool budgetCanceled, bool interrupted)
    {
        _exitCode = exitCode;
        _outcome = _userCancellation.IsCancellationRequested ? "Canceled" : budgetCanceled ? "TimedOut"
            : interrupted || exitCode != 0 ? "Failed" : "Success";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_userCancellation.IsCancellationRequested && _outcome == "StartFailed") _outcome = "Canceled";
        try
        {
            _publish(FormattableString.Invariant($"event=cloud.command.completed; category={_category}; outcome={_outcome}; exitCode={_exitCode}; elapsedMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F1}"),
                _outcome is "Success" or "Canceled" ? LogLevel.Info : LogLevel.Warning);
        }
        catch { /* Diagnostics cannot change process or credential cleanup. */ }
        finally { _scope?.Dispose(); }
    }
}
