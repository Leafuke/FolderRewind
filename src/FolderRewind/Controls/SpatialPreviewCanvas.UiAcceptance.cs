#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
using FolderRewind.Services.Development;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using System.Linq;

namespace FolderRewind.Controls;

public sealed partial class SpatialPreviewCanvas
{
    // Copy the canvas's currently installed overlays, without issuing a new index query or redraw.
    internal UiCanvasOverlaySnapshot CaptureUiAcceptanceOverlays()
        => new(_overlays.Children.Count(child => child is Border or Rectangle),
            _overlayRegions.Select(region => new UiOverlayMarker(
                new(region.Bounds.MinX, region.Bounds.MinY, region.Bounds.MaxX, region.Bounds.MaxY),
                region.Status, region.Count, region.Aggregate, region.States.ToArray(), region.Ids.ToArray(),
                region.Components.ToArray())).ToArray());
    internal object UiAcceptanceActivity() => new
    {
        wanted = _wanted.Count, pending = _pending.Count, running = _running, refinements = _refinements.Count,
        failed = _failed.Count, diagnostics = _diagnostics.Count,
        loading = _wanted.Any(k => !_failed.Contains(k) && (!_bitmaps.ContainsKey(k) || _refinements.ContainsKey(k))),
        hasWarnings = _wanted.Any(k => _failed.Contains(k) || _diagnostics.TryGetValue(k, out var d)
            && d.Severity >= FolderRewind.Plugin.Abstractions.DiagnosticSeverity.Warning)
    };
}
#endif
