using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Revoke and drain readers before disposing an index; callers retain their existing request coalescing.</summary>
internal sealed class SpatialIndexReadLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readers;
    private bool _stopping;
    internal Lease Enter(CancellationToken token)
    {
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException(_cancellation.Token);
            token.ThrowIfCancellationRequested();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cancellation.Token);
            _readers++; return new(this, linked);
        }
    }
    internal Task StopAsync()
    {
        lock (_gate) { _stopping = true; if (_readers == 0) _drained.TrySetResult(); }
        _cancellation.Cancel(); return _drained.Task;
    }
    private void Release()
    { lock (_gate) if (--_readers == 0 && _stopping) _drained.TrySetResult(); }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _cancellation.Dispose(); }
    internal sealed class Lease(SpatialIndexReadLifetime owner, CancellationTokenSource cancellation) : IDisposable
    {
        private bool _disposed;
        internal CancellationToken Token => cancellation.Token;
        public void Dispose() { if (_disposed) return; _disposed = true; cancellation.Dispose(); owner.Release(); }
    }
}
