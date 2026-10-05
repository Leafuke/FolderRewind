using System;
using System.Collections.Generic;
using System.Threading;

namespace FolderRewind.Services;

internal readonly record struct PreviewTileKey(long X, long Y, int UnitsPerPixel)
{
    public double OriginX => X * (256d * UnitsPerPixel);
    public double OriginY => Y * (256d * UnitsPerPixel);
}

/// <summary>Provider-coordinate camera, independent of UI and Minecraft.</summary>
internal sealed class SpatialPreviewViewport
{
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Scale { get; private set; } = 1;
    public void SetScale(double value) { if (double.IsFinite(value)) Scale = Math.Clamp(value, 1d / 4096, 16); }
    public (double X, double Y) ToWorld(double x, double y, double width, double height)
        => (CenterX + (x - width / 2) / Scale, CenterY + (y - height / 2) / Scale);
    public IEnumerable<PreviewTileKey> Tiles(double width, double height)
    {
        if (width <= 0 || height <= 0) yield break;
        var units = Scale >= 1 ? 1 : (int)Math.Pow(2, Math.Ceiling(Math.Log2(1 / Scale)));
        var size = 256d * units;
        var min = ToWorld(0, 0, width, height); var max = ToWorld(width, height, width, height);
        var count = 0;
        for (var y = (long)Math.Floor(min.Y / size); y <= (long)Math.Floor(max.Y / size); y++)
        for (var x = (long)Math.Floor(min.X / size); x <= (long)Math.Floor(max.X / size); x++)
        {
            if (++count > 256) yield break;
            yield return new(x, y, units);
        }
    }
}

/// <summary>Late results are invalid even if a provider ignores cancellation.</summary>
internal sealed class PreviewRequestEpoch : IDisposable
{
    private CancellationTokenSource _cancellation = new();
    public long Revision { get; private set; }
    public CancellationToken Token => _cancellation.Token;
    public long Advance()
    {
        var old = _cancellation; _cancellation = new(); Revision++;
        old.Cancel(); old.Dispose();
        return Revision;
    }
    public bool IsCurrent(long revision) => revision == Revision && !_cancellation.IsCancellationRequested;
    public void Dispose() { Revision++; _cancellation.Cancel(); _cancellation.Dispose(); }
}
