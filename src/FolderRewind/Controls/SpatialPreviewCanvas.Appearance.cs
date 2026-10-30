using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using Windows.Foundation;

namespace FolderRewind.Controls;

public sealed partial class SpatialPreviewCanvas
{
    private CompositionRoundedRectangleGeometry? _roundedGeometry;
    private CompositionGeometricClip? _roundedClip;
    internal bool UseRoundedClip { get; set; }
    private void UpdateClip()
    {
        if (!UseRoundedClip)
        {
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
            return;
        }
        // XAML's rectangular Clip and a Composition geometry must not compete for the same visual.
        if (Clip is not null) Clip = null;
        var visual = ElementCompositionPreview.GetElementVisual(this);
        _roundedGeometry ??= visual.Compositor.CreateRoundedRectangleGeometry();
        _roundedClip ??= visual.Compositor.CreateGeometricClip(_roundedGeometry);
        var radius = Application.Current.Resources["ControlCornerRadius"] is CornerRadius corners ? corners.TopLeft : 4;
        _roundedGeometry.CornerRadius = new Vector2((float)Math.Max(0, radius - 1));
        _roundedGeometry.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        visual.Clip = _roundedClip;
    }
}
