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
    public void Pan(int x, int y) { CenterX += x * 64 / Scale; CenterY += y * 64 / Scale; }
    public void SetScale(double value) { if (double.IsFinite(value)) Scale = Math.Clamp(value, 1d / 4096, 16); }
    public (double X, double Y) ToWorld(double x, double y, double width, double height)
        => (CenterX + (x - width / 2) / Scale, CenterY + (y - height / 2) / Scale);
    public (double X, double Y) ToScreen(double x, double y, double width, double height)
        => ((x - CenterX) * Scale + width / 2, (y - CenterY) * Scale + height / 2);
    public void ZoomAt(double factor, double x, double y, double width, double height)
    {
        var before = ToWorld(x, y, width, height);
        SetScale(Scale * factor);
        var after = ToWorld(x, y, width, height);
        CenterX += before.X - after.X; CenterY += before.Y - after.Y;
    }
    public void Fit(FolderRewind.Plugin.Abstractions.SpatialPreviewBounds bounds, double width, double height)
    {
        CenterX = (bounds.MinX + bounds.MaxX) / 2; CenterY = (bounds.MinY + bounds.MaxY) / 2;
        SetScale(Math.Min(Math.Max(1, width - 48) / (bounds.MaxX - bounds.MinX), Math.Max(1, height - 48) / (bounds.MaxY - bounds.MinY)));
    }
    public IEnumerable<PreviewTileKey> Tiles(double width, double height, int maximumTiles = 256)
    {
        if (width <= 0 || height <= 0) yield break;
        var units = Scale >= 1 ? 1 : (int)Math.Pow(2, Math.Ceiling(Math.Log2(1 / Scale)));
        var min = ToWorld(0, 0, width, height); var max = ToWorld(width, height, width, height);
        maximumTiles = Math.Clamp(maximumTiles, 1, 256);
        while (units < 4096)
        {
            var tileSize = 256d * units;
            var columns = Math.Floor(max.X / tileSize) - Math.Floor(min.X / tileSize) + 1;
            var rows = Math.Floor(max.Y / tileSize) - Math.Floor(min.Y / tileSize) + 1;
            if (columns * rows <= maximumTiles) break;
            units *= 2;
        }
        var size = 256d * units;
        var count = 0;
        for (var y = (long)Math.Floor(min.Y / size); y <= (long)Math.Floor(max.Y / size); y++)
        for (var x = (long)Math.Floor(min.X / size); x <= (long)Math.Floor(max.X / size); x++)
        {
            if (++count > maximumTiles) yield break;
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

internal static class SpatialPreviewCoordinates
{
    internal static bool Contains(FolderRewind.Plugin.Abstractions.SpatialPreviewBounds? bounds, double x, double y)
        => double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x) <= 1e9 && Math.Abs(y) <= 1e9
            && (bounds is null || x >= bounds.MinX && x <= bounds.MaxX && y >= bounds.MinY && y <= bounds.MaxY);
}
internal static class PreviewScaleBar
{
    internal static (double Units, double Pixels) Calculate(double scale)
    {
        var target = 100 / scale;
        var power = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var units = (target / power >= 5 ? 5 : target / power >= 2 ? 2 : 1) * power;
        return (units, units * scale);
    }
}
