using System;
using System.Threading;

namespace FolderRewind.Services;

public sealed class LatestRequestCoordinator : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeSource;
    private long _generation;
    private bool _disposed;

    public Lease Begin(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _activeSource;
            current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeSource = current;
            generation = ++_generation;
        }

        CancelAndDispose(previous);
        return new Lease(this, current, generation);
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? current;
        lock (_sync)
        {
            if (_disposed) return;
            current = _activeSource;
            _activeSource = null;
            _generation++;
        }

        CancelAndDispose(current);
    }

    public void Dispose()
    {
        CancellationTokenSource? current;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            current = _activeSource;
            _activeSource = null;
            _generation++;
        }

        CancelAndDispose(current);
    }

    private bool IsCurrent(CancellationTokenSource source, long generation)
    {
        lock (_sync)
        {
            return !_disposed
                && generation == _generation
                && ReferenceEquals(source, _activeSource)
                && !source.IsCancellationRequested;
        }
    }

    private void Complete(CancellationTokenSource source, long generation)
    {
        var dispose = false;
        lock (_sync)
        {
            if (generation == _generation && ReferenceEquals(source, _activeSource))
            {
                _activeSource = null;
                dispose = true;
            }
        }

        if (dispose)
        {
            source.Dispose();
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        try
        {
            source.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
            // Cancellation callbacks belong to the cancelled request. A faulty callback
            // must not prevent a newer request from becoming the active generation.
        }
        finally
        {
            source.Dispose();
        }
    }

    public sealed class Lease : IDisposable
    {
        private LatestRequestCoordinator? _owner;
        private readonly CancellationTokenSource _source;
        private readonly CancellationToken _token;
        private readonly long _generation;

        internal Lease(
            LatestRequestCoordinator owner,
            CancellationTokenSource source,
            long generation)
        {
            _owner = owner;
            _source = source;
            _token = source.Token;
            _generation = generation;
        }

        public CancellationToken Token => _token;

        public bool IsCurrent => _owner?.IsCurrent(_source, _generation) == true;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Complete(_source, _generation);
        }
    }
}
