using FolderRewind.History.Domain;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryMutationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private readonly Action? _validateReady;
    public HistoryMutationGate(HistoryConfigId configId, Action? validateReady = null) { ConfigId = configId; _validateReady = validateReady; }

    public HistoryConfigId ConfigId { get; }

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _validateReady?.Invoke(); return new Lease(_gate); }
        catch { _gate.Release(); throw; }
    }

    internal async ValueTask<IAsyncDisposable> EnterForRecoveryAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(token).ConfigureAwait(false); return new Lease(_gate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

