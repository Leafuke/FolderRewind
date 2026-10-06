using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

public sealed record PreviewCameraState(string LayerId, double X, double Y, double Scale, int? Height);
internal readonly record struct PreviewWorldKey(string Config, Guid Folder, string Path);

/// <summary>Most recent world layer is the first matching entry in an explicit recency list.</summary>
internal sealed class PreviewCameraStore(int capacity = 128)
{
    private readonly LinkedList<(PreviewWorldKey World, PreviewCameraState Camera)> _recent = new();
    private readonly Dictionary<(PreviewWorldKey, string), LinkedListNode<(PreviewWorldKey World, PreviewCameraState Camera)>> _entries = [];
    internal int Count => _entries.Count;
    internal void Save(PreviewWorldKey world, PreviewCameraState camera)
    {
        var key = (world, camera.LayerId);
        if (_entries.Remove(key, out var old)) _recent.Remove(old);
        _entries[key] = _recent.AddFirst((world, camera));
        while (_entries.Count > capacity && _recent.Last is { } last)
        { _entries.Remove((last.Value.World, last.Value.Camera.LayerId)); _recent.RemoveLast(); }
    }
    internal PreviewCameraState? Get(PreviewWorldKey world, string? layer = null)
        => layer is not null ? (_entries.TryGetValue((world, layer), out var node) ? node.Value.Camera : null)
            : _recent.FirstOrDefault(p => p.World == world).Camera;
}
