using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Single-flight command lifetime. Call from the UI owner context; no UI framework dependency.</summary>
internal sealed class AsyncCommandLifetime(Action<Exception> reportError) : IDisposable
{
    private CancellationTokenSource? _running;
    private bool _active = true;
    private bool _disposed;
    public bool IsBusy => _running is not null;
    public bool CanExecute => _active && !_disposed && !IsBusy;
    public event Action? StateChanged;

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = true;
        StateChanged?.Invoke();
    }

    public void Cancel() => _running?.Cancel();

    public void Deactivate()
    {
        _active = false;
        Cancel();
        StateChanged?.Invoke();
    }

    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        if (!CanExecute) return;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        StateChanged?.Invoke();
        try
        {
            running.Token.ThrowIfCancellationRequested();
            await operation(running.Token);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested) { }
        catch (Exception ex) { reportError(ex); }
        finally
        {
            _running = null;
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
    }
}
