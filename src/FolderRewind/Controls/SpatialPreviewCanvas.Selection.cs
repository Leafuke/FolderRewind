using FolderRewind.Plugin.Abstractions;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FolderRewind.Controls;

public sealed record SpatialOverlayRegion(SpatialPreviewBounds Bounds, string Status, int Count = 1)
{
    public bool Aggregate { get; init; }
    public bool Highlighted { get; init; }
    public bool Checked { get; init; }
    public string[] States { get; init; } = [];
    public string[] Ids { get; init; } = [];
    public string[] Components { get; init; } = [];
}
public sealed partial class SpatialPreviewCanvas
{
    private readonly Canvas _overlays = new() { IsHitTestVisible = false };
    private readonly Canvas _selectionVisual = new() { IsHitTestVisible = false };
    private IReadOnlyList<SpatialOverlayRegion> _overlayRegions = Array.Empty<SpatialOverlayRegion>();
    private bool _selecting;
    public bool SelectionTool { get; set; }
    public event Action<SpatialPreviewBounds, bool, bool>? SelectionRequested;
    public event Action<double, double, double>? CameraChanged;
    public SpatialPreviewBounds VisibleBounds
    {
        get
        {
            var a = _camera.ToWorld(0, 0, ActualWidth, ActualHeight); var b = _camera.ToWorld(ActualWidth, ActualHeight, ActualWidth, ActualHeight);
            return new(a.X, a.Y, b.X, b.Y);
        }
    }
    public void SetOverlays(IReadOnlyList<SpatialOverlayRegion> regions) { _overlayRegions = regions; DrawOverlays(); }
    private static bool KeyDownNow(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private void DrawSelection(Point start, Point end)
    {
        _selectionVisual.Children.Clear();
        var shape = new Rectangle { Width = Math.Abs(start.X - end.X), Height = Math.Abs(start.Y - end.Y), StrokeThickness = 2,
            Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], Fill = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] };
        Canvas.SetLeft(shape, Math.Min(start.X, end.X)); Canvas.SetTop(shape, Math.Min(start.Y, end.Y)); _selectionVisual.Children.Add(shape);
    }
    private void CancelPointer()
    { _pressed = null; _selecting = false; _selectionVisual.Children.Clear(); }
    public bool IsLeftComparisonSide { get; set; }
    public bool CompareOverlays { get; set; }
    public event Action<SpatialOverlayRegion, bool>? OverlayInvoked;
    private bool ActivateOverlay(Point point, bool twice = false)
    {
        foreach (var item in _overlayRegions.Reverse())
        {
            var a = _camera.ToScreen(item.Bounds.MinX, item.Bounds.MinY, ActualWidth, ActualHeight);
            var b = _camera.ToScreen(item.Bounds.MaxX, item.Bounds.MaxY, ActualWidth, ActualHeight);
            var center = MarkerCenter(item);
            var hit = item.Aggregate ? Math.Abs(point.X - center.X) <= MarkerWidth(item) / 2 + 3 && Math.Abs(point.Y - center.Y) <= 18
                : point.X >= a.X && point.X <= b.X && point.Y >= a.Y && point.Y <= b.Y;
            if (!hit) continue;
            OverlayInvoked?.Invoke(item, twice); return OverlayInvoked is not null;
        }
        return false;
    }
    private static double MarkerWidth(SpatialOverlayRegion item) => Math.Clamp(24 + 7 * (item.Count.ToString().Length + Math.Max(1, item.States.Length)), 40, 140);
    private Point MarkerCenter(SpatialOverlayRegion item)
    {
        var b = item.Bounds;
        var p = _camera.ToScreen((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, ActualWidth, ActualHeight);
        var half = MarkerWidth(item) / 2;
        return new(Math.Clamp(p.X, half, Math.Max(half, ActualWidth - half)), Math.Clamp(p.Y, 18, Math.Max(18, ActualHeight - 18)));
    }
    private static string Glyph(string status) => status switch
    { "Added" => "+", "Deleted" => "−", "Modified" => "~", "ContainerOnly" => "≋", "Unresolved" => "?", "Automatic" => "A", "Locked" => "⊘", "Unavailable" or "Blocked" => "!", "Selected" => "✓", "Mixed" => "±", _ => "✓" };
    private void DrawOverlays()
    {
        _overlays.Children.Clear();
        foreach (var item in _overlayRegions)
        {
            var b = item.Bounds;
            var start = _camera.ToScreen(b.MinX, b.MinY, ActualWidth, ActualHeight);
            var width = (b.MaxX - b.MinX) * _camera.Scale; var height = (b.MaxY - b.MinY) * _camera.Scale;
            if (start.X > ActualWidth || start.Y > ActualHeight || start.X + width < 0 || start.Y + height < 0) continue;
            var resource = item.Status switch
            {
                "Added" or "Resolved" => "SystemFillColorSuccessBrush", "Deleted" or "Unavailable" or "Blocked" => "SystemFillColorCriticalBrush",
                "Modified" or "Unresolved" or "Mixed" => "SystemFillColorCautionBrush", "Selected" => "AccentFillColorDefaultBrush", _ => "TextFillColorSecondaryBrush"
            };
            var stroke = (Brush)Application.Current.Resources[item.Highlighted || item.Checked ? "AccentFillColorDefaultBrush" : resource];
            var symbols = string.Join("", (item.States.Length == 0 ? new[] { item.Status } : item.States).Select(Glyph).Distinct());
            if (item.Aggregate)
            {
                var center = MarkerCenter(item); var markerWidth = MarkerWidth(item);
                var marker = new Border { Width = markerWidth, Height = 30, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(15),
                    BorderThickness = new Thickness(item.Highlighted ? 3 : 2), BorderBrush = stroke,
                    Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
                    Child = new TextBlock { Text = symbols + " " + item.Count, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] } };
                Canvas.SetLeft(marker, center.X - markerWidth / 2); Canvas.SetTop(marker, center.Y - 15); _overlays.Children.Add(marker);
                continue;
            }
            var absent = CompareOverlays && (IsLeftComparisonSide && item.Status == "Added" || !IsLeftComparisonSide && item.Status == "Deleted");
            var rectangle = new Rectangle { Width = Math.Max(2, width), Height = Math.Max(2, height),
                StrokeThickness = item.Highlighted ? 3 : item.Checked ? 2.5 : 1, Stroke = stroke,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            if (absent || item.Status is "Locked" or "Blocked" or "Unavailable") rectangle.StrokeDashArray = [3, 2];
            Canvas.SetLeft(rectangle, start.X); Canvas.SetTop(rectangle, start.Y); _overlays.Children.Add(rectangle);
            if (width >= 20 && height >= 20)
            {
                var label = new TextBlock { Text = (item.Checked ? "✓" : "") + symbols
                    + (width >= 90 && height >= 36 && item.Components.Length > 0 ? "\n" + string.Join(" · ", item.Components) : ""),
                    Width = Math.Max(1, width - 6), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = stroke };
                Canvas.SetLeft(label, start.X + 3); Canvas.SetTop(label, start.Y + 2); _overlays.Children.Add(label);
            }
        }
    }
}
