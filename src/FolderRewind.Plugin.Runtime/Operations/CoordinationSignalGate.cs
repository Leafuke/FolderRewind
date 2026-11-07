namespace FolderRewind.Plugin.Runtime.Operations;

/// <summary>Orders local submissions. A successful send is not an acknowledgement from the remote peer.</summary>
public sealed class CoordinationSignalGate
{
    private sealed record Pending(Guid? Owner, Func<CancellationToken, Task<bool>> Send);
    private const int MaximumPending = 1024;
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Pending> _pending = [];
    private Guid? _owner;
    private bool _maintenance, _needsReset;
    private CancellationTokenSource _uncertainty = new();
    public bool IsBusy { get { lock (_sync) return _owner is not null || _maintenance; } }
    public bool NeedsReset { get { lock (_sync) return _needsReset; } }
    public bool IsOwnedBy(Guid owner) { lock (_sync) return _owner == owner; }
    /// <summary>Borrow only an existing owner; never wait for or acquire a new channel under a configuration lock.</summary>
    public Admission? TryBorrow(Guid owner)
    {
        if (owner == Guid.Empty) throw new ArgumentException("Owner is required.");
        lock (_sync) return _owner == owner && !_needsReset ? new(this, owner, false) : null;
    }
    public void MarkUncertain()
    {
        CancellationTokenSource cancellation;
        lock (_sync) { _needsReset = true; cancellation = _uncertainty; }
        cancellation.Cancel();
    }

    public async ValueTask<Admission> EnterAsync(Guid owner, CancellationToken token)
    {
        if (owner == Guid.Empty) throw new ArgumentException("Owner is required.");
        lock (_sync) if (_owner == owner) return new(this, owner, false);
        await _admission.WaitAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            if (_needsReset) { _admission.Release(); throw new InvalidOperationException("Coordination channel needs reset."); }
            _owner = owner;
        }
        return new(this, owner, true);
    }

    /// <summary>Queues during coordination; idle sends also take admission, so they cannot cross a new handshake.</summary>
    public Task<bool> DispatchAsync(Guid? owner, Func<CancellationToken, Task<bool>> send, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(send);
        lock (_sync)
        {
            if (_pending.Count >= MaximumPending) { _needsReset = true; return Task.FromResult(false); }
            _pending.Add(new(owner, send));
            if (_owner is not null || _maintenance) return Task.FromResult(true);
            if (_needsReset) return Task.FromResult(false);
            // EnterAsync may have acquired admission and be waiting for _sync. It will own this queued signal.
            if (!_admission.Wait(0)) return Task.FromResult(true);
            _maintenance = true;
        }
        return DrainIdleAsync(token);
    }

    public async Task<bool> FlushOwnerAsync(Guid owner, CancellationToken token)
    {
        Pending[] selected;
        lock (_sync)
        {
            if (_owner != owner) throw new InvalidOperationException("The caller does not own the signal channel.");
            selected = _pending.Where(p => p.Owner == owner).ToArray();
            _pending.RemoveAll(p => p.Owner == owner);
        }
        return await SendAsync(selected, token).ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(Pending[] pending, CancellationToken token)
    {
        for (var index = 0; index < pending.Length; index++)
        {
            var success = false;
            try { token.ThrowIfCancellationRequested(); success = await pending[index].Send(token).ConfigureAwait(false); }
            catch (Exception) { /* Transport/provider failures cannot change a committed operation's result. */ }
            if (success) continue;
            lock (_sync)
            {
                _needsReset = true;
                // Quarantine for explicit idle reset; never retry a cancelled terminal in a later run.
                _pending.InsertRange(0, pending[index..].Take(MaximumPending - _pending.Count));
            }
            return false;
        }
        return true;
    }

    private async Task<bool> DrainAsync(CancellationToken token)
    {
        while (true)
        {
            Pending[] pending;
            lock (_sync)
            {
                if (_pending.Count == 0) return true;
                pending = _pending.ToArray(); _pending.Clear();
            }
            if (!await SendAsync(pending, token).ConfigureAwait(false)) return false;
        }
    }

    private async Task<bool> DrainIdleAsync(CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await DrainAsync(budget.Token).ConfigureAwait(false); }
        finally
        {
            lock (_sync) { if (_pending.Count != 0) _needsReset = true; _maintenance = false; }
            _admission.Release();
        }
    }

    public async Task<bool> ResetAsync(CancellationToken token, Func<CancellationToken, Task<bool>>? resetProvider = null)
    {
        if (!await _admission.WaitAsync(0, token).ConfigureAwait(false)) return false;
        lock (_sync) _maintenance = true;
        try
        {
            if (!await DrainAsync(token).ConfigureAwait(false)) return false;
            if (resetProvider is not null && !await resetProvider(token).ConfigureAwait(false)) return false;
            if (!await DrainAsync(token).ConfigureAwait(false)) return false;
            lock (_sync) { if (_pending.Count != 0) return false; _needsReset = false; _uncertainty = new(); }
            return true;
        }
        finally
        {
            lock (_sync) { if (_pending.Count != 0) _needsReset = true; _maintenance = false; }
            _admission.Release();
        }
    }

    private async ValueTask<bool> ReleaseAsync(Guid owner, CancellationToken token)
    {
        try
        {
            lock (_sync) if (_owner != owner) throw new InvalidOperationException("The caller does not own the signal channel.");
            if (NeedsReset) return false;
            return await DrainAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) { if (_pending.Count != 0) _needsReset = true; _owner = null; }
            _admission.Release();
        }
    }

    public sealed class Admission : IAsyncDisposable
    {
        private readonly CoordinationSignalGate _gate;
        private readonly Guid _owner;
        private int _disposed;
        internal Admission(CoordinationSignalGate gate, Guid owner, bool owns) { _gate = gate; _owner = owner; OwnsChannel = owns; }
        public bool OwnsChannel { get; }
        public CancellationToken Invalidated { get { lock (_gate._sync) return _gate._uncertainty.Token; } }
        public ValueTask<bool> CompleteAsync(CancellationToken token) => Interlocked.Exchange(ref _disposed, 1) == 0 && OwnsChannel
            ? _gate.ReleaseAsync(_owner, token) : ValueTask.FromResult(true);
        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await CompleteAsync(cleanup.Token).ConfigureAwait(false);
        }
    }
}
