using System.Collections.Concurrent;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Operations;

/// <summary>Runtime admission only. Providers retain ownership of computation, cancellation and cache budgets.</summary>
internal sealed class SpatialPreviewDemands
{
    private sealed class Generation(SpatialPreviewSource source)
    {
        internal readonly object Gate = new();
        internal readonly SemaphoreSlim Updates = new(1, 1);
        internal readonly string Identity = SourceIdentity(source);
        internal readonly Dictionary<Guid, SpatialPreviewDemand> Views = [];
        internal bool Closed;
    }
    private readonly ConcurrentDictionary<(string Owner, Guid Generation), Generation> _generations = new();
    internal static string SourceIdentity(SpatialPreviewSource source) => System.Text.Json.JsonSerializer.Serialize(new object?[]
    { source.Config.Kind, source.Config.ConfigId, source.Config.Revision, source.Folder.FolderId, Path.GetFullPath(source.Folder.Path),
      source.FixedInput?.Identity, source.FixedInput?.RootPath, source.FixedInput?.ContentDigest, source.IsPartial, source.CacheBudgetBytes });
    internal static bool SameTile(SpatialPreviewTileRequest a, SpatialPreviewTileRequest b)
        => a.Source.WorldId == b.Source.WorldId && a.LayerId == b.LayerId && a.OriginX == b.OriginX && a.OriginY == b.OriginY
        && a.UnitsPerPixel == b.UnitsPerPixel && a.MaximumHeight == b.MaximumHeight
        && a.ViewModeId == b.ViewModeId && a.ShowStatusOverlay == b.ShowStatusOverlay;
    internal void Open(SpatialPreviewSource source)
    {
        var state = _generations.GetOrAdd((source.Config.Kind.OwnerId.Value, source.Generation), _ => new(source));
        lock (state.Gate)
        {
            ObjectDisposedException.ThrowIf(state.Closed, this);
            if (state.Identity != SourceIdentity(source)) throw new InvalidDataException("Preview generation identity changed.");
        }
    }
    internal void EnsureNotClosed(SpatialPreviewSource source)
    {
        if (_generations.TryGetValue((source.Config.Kind.OwnerId.Value, source.Generation), out var state))
            lock (state.Gate) ObjectDisposedException.ThrowIf(state.Closed, this);
    }
    private Generation Get(SpatialPreviewSource source)
    {
        if (!_generations.TryGetValue((source.Config.Kind.OwnerId.Value, source.Generation), out var state)
            || state.Identity != SourceIdentity(source)) throw new ObjectDisposedException("Spatial preview generation");
        return state;
    }
    internal async ValueTask UpdateAsync(SpatialPreviewDemand demand, Func<ValueTask> apply, CancellationToken token)
    {
        var state = Get(demand.Source);
        await state.Updates.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (state.Gate)
            {
                ObjectDisposedException.ThrowIf(state.Closed, this);
                if (state.Views.TryGetValue(demand.ViewId, out var old))
                {
                    if (demand.Revision < old.Revision) return;
                    if (demand.Revision == old.Revision)
                    {
                        if (old.Tiles.Count != demand.Tiles.Count || old.Tiles.Where((t, i) => !SameTile(t, demand.Tiles[i])).Any())
                            throw new InvalidDataException("A preview demand revision cannot change content.");
                        return;
                    }
                }
                if (!state.Views.ContainsKey(demand.ViewId) && state.Views.Count >= 8)
                    throw new InvalidDataException("Preview view quota exceeded.");
                var all = state.Views.Where(v => v.Key != demand.ViewId).SelectMany(v => v.Value.Tiles).Concat(demand.Tiles).ToArray();
                var unique = new List<SpatialPreviewTileRequest>();
                foreach (var tile in all) if (!unique.Any(t => SameTile(t, tile))) unique.Add(tile);
                if (unique.Count > 256) throw new InvalidDataException("Preview generation tile quota exceeded.");
            }
            await apply().ConfigureAwait(false);
            lock (state.Gate)
            {
                ObjectDisposedException.ThrowIf(state.Closed, this);
                state.Views[demand.ViewId] = demand;
            }
        }
        finally { state.Updates.Release(); }
    }
    internal void Require(SpatialPreviewTileRequest request)
    {
        var state = Get(request.Source);
        lock (state.Gate)
        {
            ObjectDisposedException.ThrowIf(state.Closed, this);
            if (!state.Views.TryGetValue(request.ViewId, out var view) || request.DemandRevision > view.Revision)
                throw new InvalidOperationException("Preview render requires an applied demand revision.");
            if (!view.Tiles.Any(t => SameTile(t, request))) throw new OperationCanceledException("Preview tile is no longer requested.");
        }
    }
    internal void Close(SpatialPreviewSource source)
    {
        // Keep a tombstone until deactivation: a delayed Describe must not reopen this identity.
        var state = _generations.GetOrAdd((source.Config.Kind.OwnerId.Value, source.Generation), _ => new(source));
        lock (state.Gate) { state.Closed = true; state.Views.Clear(); }
    }
    internal void Clear(PluginId owner)
    {
        foreach (var key in _generations.Keys.Where(k => k.Owner == owner.Value))
            if (_generations.TryRemove(key, out var state)) lock (state.Gate) { state.Closed = true; state.Views.Clear(); }
    }
}
