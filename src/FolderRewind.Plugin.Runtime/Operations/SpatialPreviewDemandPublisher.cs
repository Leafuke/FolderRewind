using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Operations;

/// <summary>Single-flight latest-value publisher. Rendering starts only after the current demand was applied.</summary>
public sealed class SpatialPreviewDemandPublisher(SpatialPreviewSource source,
    Func<SpatialPreviewDemand, Task> publish, Action<Exception?> completed)
{
    private readonly object _gate = new();
    private readonly Guid _view = Guid.NewGuid();
    private SpatialPreviewDemand? _current, _pending;
    private long _applied;
    private bool _running, _closed;
    private Task _worker = Task.CompletedTask;
    public Guid ViewId => _view;
    public long Revision { get { lock (_gate) return _current?.Revision ?? 0; } }
    public bool IsReady { get { lock (_gate) return !_closed && _current is not null && _applied == _current.Revision; } }
    public void Set(IReadOnlyList<SpatialPreviewTileRequest> tiles)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_current is { } old && old.Tiles.Count == tiles.Count
                && !old.Tiles.Where((t, i) => !SpatialPreviewDemands.SameTile(t, tiles[i])).Any()) return;
            SetCore(tiles);
        }
    }
    private void SetCore(IReadOnlyList<SpatialPreviewTileRequest> tiles)
    {
        var revision = (_current?.Revision ?? 0) + 1;
        _current = _pending = new(source, _view, revision,
            Array.AsReadOnly(tiles.Select(t => t with { ViewId = _view, DemandRevision = revision }).ToArray()));
        if (!_running) { _running = true; _worker = Task.Run(PumpAsync); }
    }
    private async Task PumpAsync()
    {
        while (true)
        {
            SpatialPreviewDemand next;
            lock (_gate)
            {
                if (_pending is null) { _running = false; return; }
                next = _pending; _pending = null;
            }
            Exception? failure = null;
            try
            {
                await publish(next).ConfigureAwait(false);
                lock (_gate) _applied = next.Revision;
            }
            catch (Exception error) { failure = error; }
            // A presentation observer cannot strand the publisher or prevent its final release.
            try { completed(failure); } catch { }
        }
    }
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (!_closed) { _closed = true; SetCore([]); }
            return _worker;
        }
    }
}
