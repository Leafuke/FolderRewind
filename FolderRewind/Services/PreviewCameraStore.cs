using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

public sealed record PreviewCameraState(string LayerId, double X, double Y, double Scale, int? Height)
{
    public string? ViewModeId { get; init; }
    public bool ShowStatusOverlay { get; init; }
}
internal readonly record struct PreviewWorldKey(string Config, Guid Folder, string Path);

/// <summary>Most recent world layer is the first matching entry in an explicit recency list.</summary>
internal sealed class PreviewCameraStore(int capacity = 128)
{
    private readonly LinkedList<(PreviewWorldKey World, PreviewCameraState Camera)> _recent = new();
    private readonly Dictionary<(PreviewWorldKey, string, string?), LinkedListNode<(PreviewWorldKey World, PreviewCameraState Camera)>> _entries = [];
    internal int Count => _entries.Count;
    internal void Save(PreviewWorldKey world, PreviewCameraState camera)
    {
        var key = (world, camera.LayerId, camera.ViewModeId);
        if (_entries.Remove(key, out var old)) _recent.Remove(old);
        _entries[key] = _recent.AddFirst((world, camera));
        while (_entries.Count > capacity && _recent.Last is { } last)
        { _entries.Remove((last.Value.World, last.Value.Camera.LayerId, last.Value.Camera.ViewModeId)); _recent.RemoveLast(); }
    }
    internal PreviewCameraState? Get(PreviewWorldKey world, string? layer = null, string? mode = null)
        => _recent.FirstOrDefault(p => p.World == world && (layer is null || p.Camera.LayerId == layer)
            && (mode is null || p.Camera.ViewModeId == mode)).Camera;
}
