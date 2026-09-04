using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class MiniBackupController : IDisposable
{
    private readonly Func<string, CancellationToken, Task<OperationOutcome>> _backup;
    private readonly Func<bool> _hasChanges;
    private readonly Action<Exception> _report;
    private readonly TimeProvider _time;
    private readonly AsyncCommandLifetime _commands;
    private bool _disposed;
    public MiniWindowVisualState State { get; private set; }
    public bool CanExecute => _commands.CanExecute;
    public event Action? Changed;

    public MiniBackupController(Func<string, CancellationToken, Task<OperationOutcome>> backup, Func<bool> hasChanges,
        Action<Exception> report, TimeProvider? time = null)
    {
        _backup = backup; _hasChanges = hasChanges; _report = report; _time = time ?? TimeProvider.System;
        _commands = new(report);
        _commands.StateChanged += Notify;
    }

    public async Task BackupAsync(string comment, CancellationToken cancellationToken = default)
    {
        if (!_commands.CanExecute) return;
        await _commands.RunAsync(async token =>
        {
            SetState(MiniWindowVisualState.BackingUp);
            OperationOutcome outcome;
            try { outcome = await _backup(string.IsNullOrWhiteSpace(comment) ? "[Mini]" : $"{comment.Trim()} [Mini]", token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _report(ex); outcome = OperationOutcome.Failed; }
            token.ThrowIfCancellationRequested();
            var state = outcome switch
            {
                OperationOutcome.Success or OperationOutcome.SuccessWithWarnings => MiniWindowVisualState.BackupDone,
                OperationOutcome.NoChanges or OperationOutcome.Canceled => _hasChanges() ? MiniWindowVisualState.Changed : MiniWindowVisualState.Normal,
                _ => MiniWindowVisualState.BackupFailed
            };
            SetState(state);
            if (state is MiniWindowVisualState.BackupDone or MiniWindowVisualState.BackupFailed)
            {
                await Task.Delay(TimeSpan.FromSeconds(state == MiniWindowVisualState.BackupDone ? 2 : 3), _time, token);
                if (State == state) SetState(_hasChanges() ? MiniWindowVisualState.Changed : MiniWindowVisualState.Normal);
            }
        }, cancellationToken);
        if (State == MiniWindowVisualState.BackingUp && !_disposed)
            SetState(_hasChanges() ? MiniWindowVisualState.Changed : MiniWindowVisualState.Normal);
    }

    public void RefreshWatchState()
    {
        if (_disposed || State == MiniWindowVisualState.BackingUp) return;
        if (_hasChanges()) SetState(MiniWindowVisualState.Changed);
        else if (State == MiniWindowVisualState.Changed) SetState(MiniWindowVisualState.Normal);
    }

    private void SetState(MiniWindowVisualState state)
    {
        if (_disposed) return;
        State = state;
        Notify();
    }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.StateChanged -= Notify;
        _commands.Dispose();
        Changed = null;
    }
}
