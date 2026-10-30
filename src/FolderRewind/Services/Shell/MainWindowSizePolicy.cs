using System;

namespace FolderRewind.Services;

internal readonly record struct WindowSizeLimits(int MinimumWidth, int MinimumHeight, int MaximumWidth, int MaximumHeight);
internal readonly record struct WindowPixelSize(int Width, int Height);

internal static class MainWindowSizePolicy
{
    internal static double NormalizeScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;

    internal static WindowSizeLimits GetLimits(double scale, int workWidth, int workHeight)
    {
        scale = NormalizeScale(scale);
        var maxWidth = Math.Min((int)AppConstants.StartupWidthMax, workWidth > 0 ? workWidth : (int)AppConstants.StartupWidthMax);
        var maxHeight = Math.Min((int)AppConstants.StartupHeightMax, workHeight > 0 ? workHeight : (int)AppConstants.StartupHeightMax);
        return new((int)Math.Min(maxWidth, Math.Ceiling(AppConstants.WindowMinWidth * scale)),
            (int)Math.Min(maxHeight, Math.Ceiling(AppConstants.WindowMinHeight * scale)), maxWidth, maxHeight);
    }

    internal static WindowPixelSize Normalize(double width, double height, double scale, WindowSizeLimits limits)
    {
        scale = NormalizeScale(scale);
        static int Clamp(double value, double fallback, int min, int max)
            => (int)Math.Round(Math.Clamp(double.IsFinite(value) && value > 0 ? value : fallback, min, max));
        return new(Clamp(width, 1300 * scale, limits.MinimumWidth, limits.MaximumWidth),
            Clamp(height, 900 * scale, limits.MinimumHeight, limits.MaximumHeight));
    }
}
