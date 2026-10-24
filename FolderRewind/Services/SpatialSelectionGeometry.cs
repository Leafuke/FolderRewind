using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;

namespace FolderRewind.Services;

internal static class SpatialSelectionGeometry
{
    internal static IEnumerable<SpatialPreviewBounds> Subtract(SpatialPreviewBounds source, SpatialPreviewBounds remove)
    {
        var x1 = Math.Max(source.MinX, remove.MinX); var y1 = Math.Max(source.MinY, remove.MinY);
        var x2 = Math.Min(source.MaxX, remove.MaxX); var y2 = Math.Min(source.MaxY, remove.MaxY);
        if (x1 >= x2 || y1 >= y2) { yield return source; yield break; }
        if (source.MinY < y1) yield return new(source.MinX, source.MinY, source.MaxX, y1);
        if (y2 < source.MaxY) yield return new(source.MinX, y2, source.MaxX, source.MaxY);
        if (source.MinX < x1) yield return new(source.MinX, y1, x1, y2);
        if (x2 < source.MaxX) yield return new(x2, y1, source.MaxX, y2);
    }
}
