using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>One running read and one latest request; scan updates do not cancel useful in-flight work.</summary>
internal sealed class SpatialPresentationRefreshCoordinator<TRequest> : IAsyncDisposable
{
    private sealed record Request(TRequest Value, bool Immediate, TaskCompletionSource Completion);
    private readonly object _gate = new();
    private readonly Func<TRequest, CancellationToken, Task> _execute;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private Request? _pending;
    private CancellationTokenSource? _running;
    private Task? _worker, _dispose;
    private long _lastStarted;
    private bool _hasStarted, _disposed;

    internal SpatialPresentationRefreshCoordinator(Func<TRequest, CancellationToken, Task> execute,
        TimeSpan? interval = null, TimeProvider? clock = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _interval = interval ?? TimeSpan.FromMilliseconds(250);
        if (_interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _clock = clock ?? TimeProvider.System;
    }

    internal Task RequestAsync(TRequest value, bool immediate = true)
    {
        CancellationTokenSource? cancel;
        Task completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending?.Completion.TrySetResult();
            var request = new Request(value, immediate || _pending?.Immediate == true,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending = request;
            completion = request.Completion.Task;
            cancel = immediate ? _running : null;
            _wake.Writer.TryWrite(true);
            _worker ??= Task.Run(RunAsync);
        }
        Cancel(cancel);
        return completion;
    }

    internal void CancelPending()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            _pending?.Completion.TrySetResult();
            _pending = null;
            cancel = _running;
            _wake.Writer.TryWrite(true);
        }
        Cancel(cancel);
    }

    internal Task DrainAsync(CancellationToken token = default)
    {
        lock (_gate) return _worker?.WaitAsync(token) ?? Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Request request;
            TimeSpan delay;
            CancellationTokenSource? running = null;
            lock (_gate)
            {
                if (_disposed || _pending is null) { _worker = null; return; }
                request = _pending;
                delay = request.Immediate || !_hasStarted ? TimeSpan.Zero
                    : _interval - _clock.GetElapsedTime(_lastStarted);
                if (delay <= TimeSpan.Zero)
                {
                    _pending = null;
                    _running = running = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _lastStarted = _clock.GetTimestamp(); _hasStarted = true;
                }
            }
            if (running is null)
            {
                using var timeout = new CancellationTokenSource(delay, _clock);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);
                try { await _wake.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }
            try
            {
                await _execute(request.Value, running.Token).ConfigureAwait(false);
                request.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested)
            {
                // Superseded reads have no user-facing failure; their replacement owns publication.
                request.Completion.TrySetResult();
            }
            catch (Exception error) { request.Completion.TrySetException(error); }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_running, running)) _running = null; }
                running.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _pending?.Completion.TrySetResult(); _pending = null;
            var worker = _worker;
            return new(_dispose = Task.Run(() => StopAsync(worker)));
        }
    }
    private async Task StopAsync(Task? worker)
    {
        _lifetime.Cancel();
        if (worker is not null) await worker.ConfigureAwait(false);
        _wake.Writer.TryComplete();
        _lifetime.Dispose();
    }
    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* A completed read already released its cancellation source. */ }
    }
}
