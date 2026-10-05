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

/// <summary>Generic, viewport-sized tile surface. Domain rendering stays inside the provider.</summary>
public sealed class SpatialPreviewCanvas : Grid
{
    private readonly Canvas _tiles = new() { IsHitTestVisible = false };
    private readonly Canvas _grid = new() { IsHitTestVisible = false };
    private readonly SpatialPreviewViewport _camera = new();
    private readonly PreviewRequestEpoch _epoch = new();
    private readonly SemaphoreSlim _workers = new(4);
    private readonly Dictionary<PreviewTileKey, WriteableBitmap> _bitmaps = [];
    private readonly Dictionary<PreviewTileKey, Image> _images = [];
    private readonly LinkedList<PreviewTileKey> _lru = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private PluginV3SpatialPreview? _session;
    private SpatialPreviewLayer? _layer;
    private int? _height;
    private Point? _pressed;
    private Point _last;
    private bool _dragged;
    private bool _active;
    private long _inspection;
    public event Action<string>? StatusChanged;
    public event Action<string>? DetailsChanged;
    public event Action<double, double>? PositionChanged;

    public SpatialPreviewCanvas()
    {
        IsTabStop = true;
        Children.Add(_tiles); Children.Add(_grid);
        AutomationProperties.SetAutomationId(this, "SpatialPreviewMap");
        AutomationProperties.SetName(this, I18n.GetString("Preview_MapName"));
        SizeChanged += (_, _) => { Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) }; QueueRender(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Observe(RenderAsync()); };
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += (_, _) => _pressed = null;
        PointerCaptureLost += (_, _) => _pressed = null;
        PointerWheelChanged += (_, e) =>
        {
            Zoom(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? 2 : .5);
            e.Handled = true;
        };
        KeyDown += OnKeyDown;
        Unloaded += (_, _) => Clear();
        ActualThemeChanged += (_, _) => { if (_layer is { } layer) DrawGrid(layer); };
    }

    internal void SetSource(PluginV3SpatialPreview? session, SpatialPreviewLayer? layer, int? maximumHeight, bool reset)
    {
        _epoch.Advance(); _inspection++; _debounce.Stop();
        _session = session; _layer = layer; _height = maximumHeight; _active = session is not null && layer is not null;
        _bitmaps.Clear(); _lru.Clear(); _images.Clear(); _tiles.Children.Clear(); _grid.Children.Clear();
        if (reset && layer is not null)
        {
            _camera.CenterX = (layer.Bounds.MinX + layer.Bounds.MaxX) / 2;
            _camera.CenterY = (layer.Bounds.MinY + layer.Bounds.MaxY) / 2;
            _camera.SetScale(Math.Min(1, Math.Min(Math.Max(ActualWidth, 512) / (layer.Bounds.MaxX - layer.Bounds.MinX),
                Math.Max(ActualHeight, 512) / (layer.Bounds.MaxY - layer.Bounds.MinY))));
        }
        QueueRender();
    }
    public void Clear() => SetSource(null, null, null, false);
    public void Zoom(double factor) { _camera.SetScale(_camera.Scale * factor); QueueRender(); }
    public void Locate(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9) return;
        _camera.CenterX = x; _camera.CenterY = y; _camera.SetScale(Math.Max(1, _camera.Scale)); QueueRender();
    }
    private void QueueRender()
    {
        if (!_active) return;
        _epoch.Advance(); _debounce.Stop(); _debounce.Start();
        PositionChanged?.Invoke(_camera.CenterX, _camera.CenterY);
    }
    private async Task RenderAsync()
    {
        if (!_active || _session is not { } session || _layer is not { } layer || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (!session.IsCurrent()) { Clear(); StatusChanged?.Invoke(I18n.GetString("Preview_SourceChanged")); return; }
        var revision = _epoch.Revision; var token = _epoch.Token; var height = _height;
        var keys = _camera.Tiles(ActualWidth, ActualHeight).Where(k => k.OriginX < layer.Bounds.MaxX && k.OriginY < layer.Bounds.MaxY
            && k.OriginX + 256d * k.UnitsPerPixel > layer.Bounds.MinX && k.OriginY + 256d * k.UnitsPerPixel > layer.Bounds.MinY).ToHashSet();
        foreach (var removed in _images.Keys.Where(k => !keys.Contains(k)).ToArray())
        { _tiles.Children.Remove(_images[removed]); _images.Remove(removed); }
        foreach (var key in keys)
        {
            if (!_images.TryGetValue(key, out var image))
            {
                image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                _images.Add(key, image); _tiles.Children.Add(image);
            }
            image.Width = image.Height = 256 * key.UnitsPerPixel * _camera.Scale;
            Canvas.SetLeft(image, (key.OriginX - _camera.CenterX) * _camera.Scale + ActualWidth / 2);
            Canvas.SetTop(image, (key.OriginY - _camera.CenterY) * _camera.Scale + ActualHeight / 2);
            if (_bitmaps.TryGetValue(key, out var bitmap)) { image.Source = bitmap; Touch(key); }
        }
        DrawGrid(layer);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var pending = keys.Where(k => !_bitmaps.ContainsKey(k)).OrderBy(k => Math.Abs(k.OriginX - _camera.CenterX) + Math.Abs(k.OriginY - _camera.CenterY)).ToArray();
        StatusChanged?.Invoke(I18n.GetString(pending.Length == 0 ? (_camera.Scale < 1 ? "Preview_Overview" : "Preview_ReadOnly") : "Preview_Loading"));
        await Task.WhenAll(pending.Select(Load));
        if (_epoch.IsCurrent(revision))
            StatusChanged?.Invoke(errors.Count > 0 ? I18n.GetString("Preview_Failed") + " " + errors[0]
                : warnings.Count > 0 ? string.Join(" · ", warnings)
                : I18n.GetString(_camera.Scale < 1 ? "Preview_Overview" : "Preview_ReadOnly"));

        async Task Load(PreviewTileKey key)
        {
            var acquired = false;
            try
            {
                await _workers.WaitAsync(token); acquired = true;
                var result = await session.RenderAsync(new(session.Source, layer.Id, key.OriginX, key.OriginY, key.UnitsPerPixel, height), token);
                if (!_epoch.IsCurrent(revision) || !session.IsCurrent()) return;
                var bitmap = new WriteableBitmap(256, 256);
                using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(result.BgraPremultiplied.Span);
                bitmap.Invalidate();
                _bitmaps[key] = bitmap; Touch(key);
                if (_images.TryGetValue(key, out var image)) image.Source = bitmap;
                foreach (var warning in result.Diagnostics)
                    warnings.Add(I18n.PickBest(warning.Arguments, warning.Code) ?? warning.Code);
                Trim(keys);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (_epoch.IsCurrent(revision)) errors.Add(ex.Message); }
            finally { if (acquired) _workers.Release(); }
        }
    }
    private void Touch(PreviewTileKey key) { _lru.Remove(key); _lru.AddFirst(key); }
    private void Trim(HashSet<PreviewTileKey> visible)
    {
        // Each 256px bitmap is 256 KiB; visible tiles are included in the 64 MiB budget.
        while (_bitmaps.Count > 256 && _lru.Last is { } last)
        {
            var candidate = last;
            while (candidate is not null && visible.Contains(candidate.Value)) candidate = candidate.Previous;
            if (candidate is null) break;
            _bitmaps.Remove(candidate.Value); _lru.Remove(candidate);
        }
    }
    private void DrawGrid(SpatialPreviewLayer layer)
    {
        _grid.Children.Clear();
        var interval = layer.MinorGridSize * _camera.Scale >= 24 ? layer.MinorGridSize : layer.MajorGridSize;
        if (interval * _camera.Scale < 16) return;
        var brush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var corner = _camera.ToWorld(0, 0, ActualWidth, ActualHeight);
        for (var x = Math.Ceiling(corner.X / interval) * interval; (x - corner.X) * _camera.Scale < ActualWidth; x += interval)
            _grid.Children.Add(new Line { X1 = (x - corner.X) * _camera.Scale, X2 = (x - corner.X) * _camera.Scale, Y1 = 0, Y2 = ActualHeight, Stroke = brush, Opacity = .35, StrokeThickness = 1 });
        for (var y = Math.Ceiling(corner.Y / interval) * interval; (y - corner.Y) * _camera.Scale < ActualHeight; y += interval)
            _grid.Children.Add(new Line { Y1 = (y - corner.Y) * _camera.Scale, Y2 = (y - corner.Y) * _camera.Scale, X1 = 0, X2 = ActualWidth, Stroke = brush, Opacity = .35, StrokeThickness = 1 });
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_active) return;
        Focus(FocusState.Pointer); _pressed = _last = e.GetCurrentPoint(this).Position; _dragged = false; CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed is not { } pressed) return;
        var point = e.GetCurrentPoint(this).Position;
        _dragged |= Math.Abs(point.X - pressed.X) + Math.Abs(point.Y - pressed.Y) > 4;
        _camera.CenterX -= (point.X - _last.X) / _camera.Scale; _camera.CenterY -= (point.Y - _last.Y) / _camera.Scale;
        _last = point; QueueRender(); e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        var inspect = _pressed is not null && !_dragged;
        var point = e.GetCurrentPoint(this).Position;
        _pressed = null; ReleasePointerCapture(e.Pointer);
        if (inspect) Observe(InspectAsync(point.X, point.Y));
        e.Handled = true;
    }
    private async Task InspectAsync(double x, double y)
    {
        if (_session is not { } session || _layer is not { } layer) return;
        var revision = _epoch.Revision; var inspection = ++_inspection; var token = _epoch.Token;
        var point = _camera.ToWorld(x, y, ActualWidth, ActualHeight);
        var acquired = false;
        try
        {
            await _workers.WaitAsync(token); acquired = true;
            var result = await session.InspectAsync(new(session.Source, layer.Id, point.X, point.Y, _height), token);
            if (_epoch.IsCurrent(revision) && inspection == _inspection && session.IsCurrent())
                DetailsChanged?.Invoke(string.Join(Environment.NewLine, result.Fields.Select(f => PluginV3SpatialPreview.Localize(f.DisplayName) + ": " + PluginV3SpatialPreview.Localize(f.Value))));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_epoch.IsCurrent(revision)) StatusChanged?.Invoke(I18n.GetString("Preview_Failed") + " " + ex.Message); }
        finally { if (acquired) _workers.Release(); }
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = 64 / _camera.Scale;
        switch (e.Key)
        {
            case VirtualKey.Left: _camera.CenterX -= step; break;
            case VirtualKey.Right: _camera.CenterX += step; break;
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
