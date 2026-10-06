using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace FolderRewind.Controls;

/// <summary>View-owned image loading. Cache and XAML objects are confined to the UI thread.</summary>
public sealed partial class AsyncFileImage : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string),
        typeof(AsyncFileImage), new PropertyMetadata(null, (owner, _) => ((AsyncFileImage)owner).Reload(true)));
    public static readonly DependencyProperty DecodeDipProperty = DependencyProperty.Register(nameof(DecodeDip), typeof(double),
        typeof(AsyncFileImage), new PropertyMetadata(0d, (owner, _) => ((AsyncFileImage)owner).Reload(false)));
    public static readonly DependencyProperty ReloadVersionProperty = DependencyProperty.Register(nameof(ReloadVersion), typeof(int),
        typeof(AsyncFileImage), new PropertyMetadata(0, (owner, _) => ((AsyncFileImage)owner).Reload(false)));
    public int ReloadVersion { get => (int)GetValue(ReloadVersionProperty); set => SetValue(ReloadVersionProperty, value); }
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch), typeof(Stretch),
        typeof(AsyncFileImage), new PropertyMetadata(Stretch.UniformToFill, (owner, args) =>
        { var control = (AsyncFileImage)owner; control._image.Stretch = (Stretch)args.NewValue; control.Reload(false); }));
    public string? SourcePath { get => (string?)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public double DecodeDip { get => (double)GetValue(DecodeDipProperty); set => SetValue(DecodeDipProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private CancellationTokenSource? _load;
    private XamlRoot? _root;
    private bool _loaded;
    private long _generation;
    private static readonly Dictionary<string, LinkedListNode<CacheItem>> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheItem> Recent = new();
    private static long _cacheBytes;
    private const long CacheLimit = 32 * 1024 * 1024;
    private sealed record CacheItem(string Key, BitmapImage Image, long Bytes);

    public AsyncFileImage()
    {
        Content = _image;
        Loaded += (_, _) => { _loaded = true; _root = XamlRoot; if (_root is not null) _root.Changed += RootChanged; Reload(false); };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            if (_root is not null) _root.Changed -= RootChanged;
            _root = null;
            Cancel(); _image.Source = null;
        };
        SizeChanged += (_, _) => { if (DecodeDip <= 0) Reload(false); };
    }

    private double _lastScale;
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_lastScale == sender.RasterizationScale) return;
        Reload(false);
    }

    private void Cancel()
    {
        ++_generation;
        _load?.Cancel(); _load?.Dispose(); _load = null;
    }

    private void Reload(bool clear)
    {
        Cancel();
        if (clear) _image.Source = null;
        if (!_loaded || string.IsNullOrWhiteSpace(SourcePath)) return;
        _lastScale = XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Max(1, (int)Math.Ceiling((DecodeDip > 0 ? DecodeDip : ActualWidth) * _lastScale));
        var height = Math.Max(1, (int)Math.Ceiling((DecodeDip > 0 ? DecodeDip : ActualHeight) * _lastScale));
        _load = new CancellationTokenSource();
        _ = LoadAsync(SourcePath, width, height, _generation, _load.Token);
    }

    private async Task LoadAsync(string path, int width, int height, long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(100, token);
            var stamp = await Task.Run(() =>
            {
                var file = new FileInfo(path);
                return (Path: file.FullName, file.Length, Ticks: file.LastWriteTimeUtc.Ticks);
            }, token);
            var key = $"{stamp.Path}|{stamp.Length}|{stamp.Ticks}|{width}|{height}|{Stretch}";
            if (token.IsCancellationRequested || generation != _generation) return;
            if (Cache.TryGetValue(key, out var cached))
            {
                Recent.Remove(cached); Recent.AddFirst(cached);
                _image.Source = cached.Value.Image;
                return;
            }
            var bytes = await Task.Run(() => File.ReadAllBytesAsync(stamp.Path, token), token);
            using var memory = new MemoryStream(bytes, writable: false);
            using var stream = memory.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var factor = Stretch == Stretch.UniformToFill
                ? Math.Max((double)width / decoder.PixelWidth, (double)height / decoder.PixelHeight)
                : Math.Min((double)width / decoder.PixelWidth, (double)height / decoder.PixelHeight);
            if (Stretch == Stretch.None) factor = 1;
            factor = Math.Min(1, factor);
            var decodeWidth = Math.Max(1, (int)Math.Ceiling(decoder.PixelWidth * factor));
            var decodeHeight = Math.Max(1, (int)Math.Ceiling(decoder.PixelHeight * factor));
            if (token.IsCancellationRequested || generation != _generation) return;
            var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelHeight = decodeHeight };
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream).AsTask(token);
            if (token.IsCancellationRequested || generation != _generation || !_loaded) return;
            _image.Source = bitmap;
            var cost = (long)decodeWidth * decodeHeight * 4;
            if (cost > CacheLimit) return;
            if (Cache.Remove(key, out var existing)) { Recent.Remove(existing); _cacheBytes -= existing.Value.Bytes; }
            var node = Recent.AddFirst(new CacheItem(key, bitmap, cost));
            Cache.Add(key, node); _cacheBytes += cost;
            while (_cacheBytes > CacheLimit && Recent.Last is { } last)
            { Cache.Remove(last.Value.Key); _cacheBytes -= last.Value.Bytes; Recent.RemoveLast(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && generation == _generation)
                System.Diagnostics.Debug.WriteLine($"Image load failed: {error.Message}");
        }
    }
}
