using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;

namespace FolderRewind.Controls;

/// <summary>Immediate camera transforms with independently throttled, bounded tile loading.</summary>
public sealed class SpatialPreviewCanvas : Grid
{
    private readonly Canvas _tiles = new() { IsHitTestVisible = false };
    private readonly Canvas _grid = new() { IsHitTestVisible = false };
    private readonly Canvas _markers = new() { IsHitTestVisible = false };
    private readonly CompositeTransform _transform = new();
    private readonly CompositeTransform _gridTransform = new();
    private double _gridX, _gridY, _gridScale = 1;
    private Ellipse? _markerShape;
    private readonly SpatialPreviewViewport _camera = new();
    private readonly PreviewRequestEpoch _epoch = new();
    private readonly Dictionary<PreviewTileKey, WriteableBitmap> _bitmaps = [];
    private readonly Dictionary<PreviewTileKey, Image> _images = [];
    private readonly LinkedList<PreviewTileKey> _lru = new();
    private readonly Dictionary<PreviewTileKey, CancellationTokenSource> _pending = [];
    private readonly Dictionary<PreviewTileKey, string> _diagnostics = [];
    private readonly HashSet<PreviewTileKey> _failed = [];
    private HashSet<PreviewTileKey> _wanted = [];
    private readonly DispatcherTimer _schedule = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private PluginV3SpatialPreview? _session;
    private SpatialPreviewLayer? _layer;
    private int? _height;
    private Point? _pressed;
    private Point _last;
    private bool _dragged;
    private bool _active;
    private bool _showGrid;
    private int _running;
    private long _inspection;
    private double _anchorX, _anchorY;
    private Point? _marker;
    public event Action? Interacted;
    public event Action<string>? StatusChanged;
    public event Action<string>? DetailsChanged;
    public event Action<double, double>? PositionChanged;
    public event Action<double, double>? CursorChanged;
    internal (double X, double Y, double Scale) Camera => (_camera.CenterX, _camera.CenterY, _camera.Scale);
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; DrawGrid(); } }

    public SpatialPreviewCanvas()
    {
        IsTabStop = true;
        _tiles.RenderTransform = _transform; _grid.RenderTransform = _gridTransform;
        Children.Add(_tiles); Children.Add(_grid); Children.Add(_markers);
        AutomationProperties.SetAutomationId(this, "SpatialPreviewMap");
        AutomationProperties.SetName(this, I18n.GetString("Preview_MapName"));
        SizeChanged += (_, _) => { Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) }; QueueRender(); };
        _schedule.Tick += (_, _) => { _schedule.Stop(); Reconcile(); };
        PointerPressed += Pressed; PointerMoved += Moved; PointerReleased += Released;
        PointerCanceled += (_, _) => _pressed = null;
        PointerCaptureLost += (_, _) => _pressed = null;
        PointerWheelChanged += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            ZoomAt(point.Properties.MouseWheelDelta > 0 ? 2 : .5, point.Position);
            e.Handled = true;
        };
        KeyDown += OnKeyDown;
        Unloaded += (_, _) => Clear();
        ActualThemeChanged += (_, _) => { DrawGrid(); DrawMarker(); };
    }
    internal void SetSource(PluginV3SpatialPreview? session, SpatialPreviewLayer? layer, int? maximumHeight, bool reset)
    {
        _epoch.Advance(); _inspection++; _schedule.Stop();
        foreach (var cancellation in _pending.Values.ToArray()) cancellation.Cancel();
        _pending.Clear(); _wanted.Clear(); _failed.Clear(); _diagnostics.Clear();
        _session = session; _layer = layer; _height = maximumHeight; _active = session is not null && layer is not null;
        _bitmaps.Clear(); _lru.Clear(); _images.Clear(); _tiles.Children.Clear(); _grid.Children.Clear(); _markers.Children.Clear(); _markerShape = null;
        _pressed = null; ReleasePointerCaptures(); _marker = null;
        if (reset && layer is not null) FitCamera(layer);
        _anchorX = _camera.CenterX; _anchorY = _camera.CenterY;
        QueueRender();
    }
    public void Clear() => SetSource(null, null, null, false);
    private void FitCamera(SpatialPreviewLayer layer)
    {
        _camera.CenterX = (layer.Bounds.MinX + layer.Bounds.MaxX) / 2;
        _camera.CenterY = (layer.Bounds.MinY + layer.Bounds.MaxY) / 2;
        _camera.SetScale(Math.Min(1, Math.Min(Math.Max(ActualWidth, 512) / (layer.Bounds.MaxX - layer.Bounds.MinX),
            Math.Max(ActualHeight, 512) / (layer.Bounds.MaxY - layer.Bounds.MinY))));
    }
    public void Fit() { if (_layer is { } layer) { Interacted?.Invoke(); FitCamera(layer); QueueRender(); } }
    internal void RestoreCamera(double x, double y, double scale)
    { _camera.CenterX = x; _camera.CenterY = y; _camera.SetScale(scale); QueueRender(); }
    public void Zoom(double factor) => ZoomAt(factor, new(ActualWidth / 2, ActualHeight / 2));
    private void ZoomAt(double factor, Point point)
    {
        if (!_active) return;
        Interacted?.Invoke();
        var before = _camera.ToWorld(point.X, point.Y, ActualWidth, ActualHeight);
        _camera.SetScale(_camera.Scale * factor);
        var after = _camera.ToWorld(point.X, point.Y, ActualWidth, ActualHeight);
        _camera.CenterX += before.X - after.X; _camera.CenterY += before.Y - after.Y;
        QueueRender();
    }
    public void Locate(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9) return;
        Interacted?.Invoke();
        _camera.CenterX = x; _camera.CenterY = y; _camera.SetScale(Math.Max(1, _camera.Scale));
        _marker = new(x, y); QueueRender();
    }
    private void QueueRender()
    {
        if (!_active) return;
        // Move already uploaded pixels synchronously; disk work is never on the input path.
        _transform.ScaleX = _transform.ScaleY = _camera.Scale;
        _transform.TranslateX = (_anchorX - _camera.CenterX) * _camera.Scale + ActualWidth / 2;
        _transform.TranslateY = (_anchorY - _camera.CenterY) * _camera.Scale + ActualHeight / 2;
        _gridTransform.ScaleX = _gridTransform.ScaleY = _camera.Scale / _gridScale;
        _gridTransform.TranslateX = (_gridX - _camera.CenterX) * _camera.Scale + ActualWidth / 2 * (1 - _camera.Scale / _gridScale);
        _gridTransform.TranslateY = (_gridY - _camera.CenterY) * _camera.Scale + ActualHeight / 2 * (1 - _camera.Scale / _gridScale);
        DrawMarker();
        PositionChanged?.Invoke(_camera.CenterX, _camera.CenterY);
        if (!_schedule.IsEnabled) _schedule.Start(); // Throttle, not a restart-on-every-move debounce.
    }
    private bool Intersects(PreviewTileKey key)
    {
        var min = _camera.ToWorld(0, 0, ActualWidth, ActualHeight);
        var max = _camera.ToWorld(ActualWidth, ActualHeight, ActualWidth, ActualHeight);
        var size = 256d * key.UnitsPerPixel;
        return key.OriginX < max.X && key.OriginY < max.Y && key.OriginX + size > min.X && key.OriginY + size > min.Y;
    }
    private void Reconcile()
    {
        if (!_active || _session is not { } session || _layer is not { } layer || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (!session.IsCurrent()) { Clear(); StatusChanged?.Invoke(I18n.GetString("Preview_SourceChanged")); return; }
        _wanted = _camera.Tiles(ActualWidth, ActualHeight).Where(k => k.OriginX < layer.Bounds.MaxX && k.OriginY < layer.Bounds.MaxY
            && k.OriginX + 256d * k.UnitsPerPixel > layer.Bounds.MinX && k.OriginY + 256d * k.UnitsPerPixel > layer.Bounds.MinY).ToHashSet();
        foreach (var pair in _pending.ToArray()) if (!_wanted.Contains(pair.Key)) pair.Value.Cancel();
        _failed.RemoveWhere(k => !_wanted.Contains(k));
        if (_wanted.Any(k => !_bitmaps.ContainsKey(k)))
            foreach (var key in _bitmaps.Keys.Where(k => !_wanted.Contains(k) && Intersects(k)).OrderByDescending(k => k.UnitsPerPixel).ToArray()) Show(key);
        foreach (var key in _wanted) if (_bitmaps.ContainsKey(key)) Show(key);
        PruneImages(); Trim(); DrawGrid(); Pump(); UpdateStatus();
    }
    private void Show(PreviewTileKey key)
    {
        if (!_bitmaps.TryGetValue(key, out var bitmap)) return;
        if (!_images.TryGetValue(key, out var image))
        {
            image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false,
                Width = 256d * key.UnitsPerPixel, Height = 256d * key.UnitsPerPixel };
            Canvas.SetLeft(image, key.OriginX - _anchorX); Canvas.SetTop(image, key.OriginY - _anchorY);
            _images.Add(key, image); _tiles.Children.Add(image);
        }
        Canvas.SetZIndex(image, _wanted.Contains(key) ? 1 : 0);
        image.Source = bitmap; Touch(key);
    }
    private void PruneImages()
    {
        var complete = _wanted.All(k => _bitmaps.ContainsKey(k) || _failed.Contains(k));
        foreach (var pair in _images.ToArray())
        {
            if (!Intersects(pair.Key) || (complete && !_wanted.Contains(pair.Key))) RemoveImage(pair.Key);
            else Canvas.SetZIndex(pair.Value, _wanted.Contains(pair.Key) ? 1 : 0);
        }
    }
    private void RemoveImage(PreviewTileKey key)
    {
        if (_images.Remove(key, out var image)) { image.Source = null; _tiles.Children.Remove(image); }
    }
    private void Pump()
    {
        if (!_active || _session is not { } session || _layer is not { } layer) return;
        foreach (var key in _wanted.Where(k => !_bitmaps.ContainsKey(k) && !_pending.ContainsKey(k) && !_failed.Contains(k))
            .OrderBy(k => Math.Abs(k.OriginX + 128d * k.UnitsPerPixel - _camera.CenterX) + Math.Abs(k.OriginY + 128d * k.UnitsPerPixel - _camera.CenterY)).ToArray())
        {
            if (_running >= 4) break;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_epoch.Token);
            _pending.Add(key, cancellation); _running++;
            Observe(LoadAsync(session, layer.Id, _height, key, cancellation, _epoch.Revision));
        }
    }
    private async Task LoadAsync(PluginV3SpatialPreview session, string layer, int? height, PreviewTileKey key, CancellationTokenSource cancellation, long revision)
    {
        try
        {
            var result = await session.RenderAsync(new(session.Source, layer, key.OriginX, key.OriginY, key.UnitsPerPixel, height), cancellation.Token);
            if (cancellation.IsCancellationRequested || !_epoch.IsCurrent(revision) || !session.IsCurrent()) return;
            var bitmap = new WriteableBitmap(256, 256);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(result.BgraPremultiplied.Span);
            bitmap.Invalidate(); _bitmaps[key] = bitmap; Touch(key);
            if (result.Diagnostics.Count > 0) _diagnostics[key] = string.Join(" · ", result.Diagnostics.Select(d => I18n.PickBest(d.Arguments, d.Code) ?? d.Code));
            if (_wanted.Contains(key)) Show(key);
            PruneImages(); Trim();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_epoch.IsCurrent(revision) && !cancellation.IsCancellationRequested)
            { _failed.Add(key); _diagnostics[key] = I18n.GetString("Preview_Failed") + " " + ex.Message; }
        }
        finally
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, cancellation)) _pending.Remove(key);
            cancellation.Dispose(); _running--;
            if (_active) { Pump(); UpdateStatus(); }
        }
    }
    private void UpdateStatus()
    {
        if (!_active) return;
        var diagnostic = _wanted.Where(_diagnostics.ContainsKey).Select(k => _diagnostics[k]).FirstOrDefault();
        StatusChanged?.Invoke(diagnostic ?? I18n.GetString(_wanted.Any(k => !_bitmaps.ContainsKey(k) && !_failed.Contains(k))
            ? "Preview_Loading" : _wanted.Any(k => k.UnitsPerPixel >= 16) ? "Preview_Overview" : "Preview_ReadOnly"));
    }
    private void Touch(PreviewTileKey key) { _lru.Remove(key); _lru.AddFirst(key); }
    private void Trim()
    {
        // Every cached 256px bitmap (including fallback LODs) is counted in the 64 MiB budget.
        while (_bitmaps.Count > 256 && _lru.Last is { } last)
        {
            var candidate = last;
            while (candidate is not null && _wanted.Contains(candidate.Value)) candidate = candidate.Previous;
            if (candidate is null) break;
            RemoveImage(candidate.Value); _bitmaps.Remove(candidate.Value); _diagnostics.Remove(candidate.Value); _lru.Remove(candidate);
        }
        foreach (var key in _diagnostics.Keys.Where(k => !_wanted.Contains(k) && !_bitmaps.ContainsKey(k)).ToArray()) _diagnostics.Remove(key);
    }
    private void DrawGrid()
    {
        _grid.Children.Clear();
        _gridX = _camera.CenterX; _gridY = _camera.CenterY; _gridScale = _camera.Scale;
        _gridTransform.ScaleX = _gridTransform.ScaleY = 1;
        _gridTransform.TranslateX = _gridTransform.TranslateY = 0;
        if (!_showGrid || !_active || _layer is not { } layer) return;
        var interval = layer.MinorGridSize * _camera.Scale >= 24 ? layer.MinorGridSize : layer.MajorGridSize;
        if (interval * _camera.Scale < 16) return;
        var brush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var corner = _camera.ToWorld(0, 0, ActualWidth, ActualHeight);
        for (var x = Math.Ceiling((corner.X - 128 / _camera.Scale) / interval) * interval; (x - corner.X) * _camera.Scale < ActualWidth + 128; x += interval)
            _grid.Children.Add(new Line { X1 = (x - corner.X) * _camera.Scale, X2 = (x - corner.X) * _camera.Scale, Y1 = -128, Y2 = ActualHeight + 128, Stroke = brush, Opacity = .18, StrokeThickness = 1 });
        for (var y = Math.Ceiling((corner.Y - 128 / _camera.Scale) / interval) * interval; (y - corner.Y) * _camera.Scale < ActualHeight + 128; y += interval)
            _grid.Children.Add(new Line { Y1 = (y - corner.Y) * _camera.Scale, Y2 = (y - corner.Y) * _camera.Scale, X1 = -128, X2 = ActualWidth + 128, Stroke = brush, Opacity = .18, StrokeThickness = 1 });
    }
    private void DrawMarker()
    {
        if (_marker is not { } marker) return;
        if (_markerShape is null)
        {
            _markerShape = new Ellipse { Width = 16, Height = 16, StrokeThickness = 3 };
            _markers.Children.Add(_markerShape);
        }
        _markerShape.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _markerShape.Fill = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        Canvas.SetLeft(_markerShape, (marker.X - _camera.CenterX) * _camera.Scale + ActualWidth / 2 - 8);
        Canvas.SetTop(_markerShape, (marker.Y - _camera.CenterY) * _camera.Scale + ActualHeight / 2 - 8);
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_active || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer); _pressed = _last = e.GetCurrentPoint(this).Position; _dragged = false; CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_active) return;
        var point = e.GetCurrentPoint(this).Position;
        if (_pressed is { } pressed)
        {
            Interacted?.Invoke();
            _dragged |= Math.Abs(point.X - pressed.X) + Math.Abs(point.Y - pressed.Y) > 4;
            _camera.CenterX -= (point.X - _last.X) / _camera.Scale; _camera.CenterY -= (point.Y - _last.Y) / _camera.Scale;
            _last = point; QueueRender(); e.Handled = true;
        }
        var world = _camera.ToWorld(point.X, point.Y, ActualWidth, ActualHeight);
        CursorChanged?.Invoke(world.X, world.Y);
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        var inspect = _pressed is not null && !_dragged; var point = e.GetCurrentPoint(this).Position;
        _pressed = null; ReleasePointerCapture(e.Pointer);
        if (inspect) Observe(InspectAsync(point.X, point.Y));
        e.Handled = true;
    }
    private async Task InspectAsync(double x, double y)
    {
        if (_session is not { } session || _layer is not { } layer) return;
        var revision = _epoch.Revision; var inspection = ++_inspection; var token = _epoch.Token;
        var point = _camera.ToWorld(x, y, ActualWidth, ActualHeight);
        try
        {
            var result = await session.InspectAsync(new(session.Source, layer.Id, point.X, point.Y, _height), token);
            if (_epoch.IsCurrent(revision) && inspection == _inspection && session.IsCurrent())
                DetailsChanged?.Invoke(string.Join(Environment.NewLine, result.Fields.Select(f => PluginV3SpatialPreview.Localize(f.DisplayName) + ": " + PluginV3SpatialPreview.Localize(f.Value))));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_epoch.IsCurrent(revision)) StatusChanged?.Invoke(I18n.GetString("Preview_Failed") + " " + ex.Message); }
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_active) return;
        Interacted?.Invoke();
        var step = 64 / _camera.Scale;
        switch (e.Key)
        {
            case VirtualKey.Left: _camera.CenterX -= step; break;
            case VirtualKey.Right: _camera.CenterY -= step; break;
            case VirtualKey.Up: _camera.CenterY -= step; break;
            case VirtualKey.Down: _camera.CenterY += step; break;
            case VirtualKey.Add: Zoom(2); e.Handled = true; return;
            case VirtualKey.Subtract: Zoom(.5); e.Handled = true; return;
            case VirtualKey.Enter: Observe(InspectAsync(ActualWidth / 2, ActualHeight / 2)); e.Handled = true; return;
            default: return;
        }
        QueueRender(); e.Handled = true;
    }
    private void Observe(Task task) => TaskObserver.Observe(task, nameof(SpatialPreviewCanvas));
}
