using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Operations;

/// <summary>Short-lived leases and frozen output. A browsing page never pins a plugin session.</summary>
public sealed class SpatialPreviewDispatcher(PluginRuntimeManager runtime)
{
    public const int TileSize = 256;

    public async ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, CancellationToken token)
    {
        using var lease = Acquire(source, token);
        var value = await lease.Capability.DescribeAsync(source, lease.Context).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (value.Layers.Count > 128 || value.Layers.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != value.Layers.Count)
            throw new InvalidDataException("Invalid preview layer inventory.");
        var layers = value.Layers.Select(l =>
        {
            if (string.IsNullOrWhiteSpace(l.Id) || l.Id.Length > 256 || !Finite(l.Bounds.MinX, l.Bounds.MinY, l.Bounds.MaxX, l.Bounds.MaxY, l.MinorGridSize, l.MajorGridSize)
                || l.Bounds.MinX >= l.Bounds.MaxX || l.Bounds.MinY >= l.Bounds.MaxY || l.MinorGridSize <= 0 || l.MajorGridSize < l.MinorGridSize
                || (l.Height is { } h && (h.Minimum > h.DefaultValue || h.DefaultValue > h.Maximum)))
                throw new InvalidDataException("Invalid preview layer.");
            return l with { Name = Freeze(l.Name), Height = l.Height is { } height ? height with { Label = Freeze(height.Label) } : null };
        }).ToArray();
        return new(Array.AsReadOnly(layers), Freeze(value.HorizontalAxis), Freeze(value.VerticalAxis), Freeze(value.Diagnostics))
        { LayerLabel = value.LayerLabel is { } label ? Freeze(label) : null,
          NavigationLabel = value.NavigationLabel is { } navigation ? Freeze(navigation) : null,
          CellLabel = value.CellLabel is { } cell ? Freeze(cell) : null };
    }

    public async ValueTask<SpatialPreviewNavigation> GetNavigationTargetsAsync(SpatialPreviewSource source, CancellationToken token)
    {
        using var lease = Acquire(source, token);
        var value = await lease.Capability.GetNavigationTargetsAsync(source, lease.Context).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (value.Targets.Count > 4096 || value.Targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != value.Targets.Count
            || (value.DefaultTargetId is { } id && !value.Targets.Any(t => t.Id == id))
            || (value.QuickTargetId is { } quick && !value.Targets.Any(t => t.Id == quick)))
            throw new InvalidDataException("Invalid preview navigation inventory.");
        var targets = value.Targets.Select(t =>
        {
            if (string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > 256 || string.IsNullOrWhiteSpace(t.LayerId)
                || t.LayerId.Length > 256 || !Finite(t.X, t.Y)) throw new InvalidDataException("Invalid preview target.");
            return t with { Group = Freeze(t.Group), Name = Freeze(t.Name) };
        }).ToArray();
        return new(Array.AsReadOnly(targets), value.DefaultTargetId) { QuickTargetId = value.QuickTargetId };
    }

    public async ValueTask CloseAsync(SpatialPreviewSource source)
    {
        // A stopped plugin has already cleared its caches in Deactivate. Do not pin it or reactivate it for cleanup.
        using var lease = runtime.TryAcquire<ISpatialPreviewCapability>(new(source.Config.Kind.OwnerId.Value), c => c.Kind == source.Config.Kind);
        if (lease is not null) await lease.Capability.CloseAsync(source, lease.Context).ConfigureAwait(false);
    }

    public async ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, CancellationToken token)
    {
        if (!Finite(request.OriginX, request.OriginY, request.UnitsPerPixel) || request.UnitsPerPixel is < 1 or > 4096)
            throw new ArgumentException("Invalid preview tile coordinates.");
        using var lease = Acquire(request.Source, token);
        var value = await lease.Capability.RenderAsync(request, lease.Context).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return FreezeTile(value);
    }

    public async ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, CancellationToken token)
    {
        if (!Finite(request.X, request.Y)) throw new ArgumentException("Invalid preview point.");
        using var lease = Acquire(request.Source, token);
        var value = await lease.Capability.InspectAsync(request, lease.Context).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (value.Fields.Count > 64) throw new InvalidDataException("Preview detail quota exceeded.");
        return new(Array.AsReadOnly(value.Fields.Select(f => f with { DisplayName = Freeze(f.DisplayName), Value = Freeze(f.Value) }).ToArray()), Freeze(value.Diagnostics));
    }

    public static SpatialPreviewTile FreezeTile(SpatialPreviewTile value)
    {
        if (value.Width != TileSize || value.Height != TileSize || value.BgraPremultiplied.Length != TileSize * TileSize * 4)
            throw new InvalidDataException("Invalid preview raster dimensions or buffer length.");
        return new(TileSize, TileSize, value.BgraPremultiplied.ToArray(), Freeze(value.Diagnostics));
    }

    private PluginCapabilityLease<ISpatialPreviewCapability> Acquire(SpatialPreviewSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Generation == Guid.Empty || !source.Config.Folders.Any(f => f.FolderId == source.Folder.FolderId && f.Path == source.Folder.Path))
            throw new ArgumentException("Preview source does not belong to the configuration snapshot.");
        return runtime.TryAcquire<ISpatialPreviewCapability>(new(source.Config.Kind.OwnerId.Value), c => c.Kind == source.Config.Kind, token)
            ?? throw new InvalidOperationException("SpatialPreviewUnavailable");
    }

    private static bool Finite(params double[] values) => values.All(v => double.IsFinite(v) && Math.Abs(v) <= 1e12);
    private static LocalizedText Freeze(LocalizedText value)
    {
        if (value.Default.Length > 4096 || value.Translations.Count > 32 || value.Translations.Any(p => p.Value.Length > 4096))
            throw new InvalidDataException("Preview text quota exceeded.");
        return new(value.Default, value.Translations.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
    private static IReadOnlyList<PluginDiagnostic> Freeze(IReadOnlyList<PluginDiagnostic> values)
    {
        if (values.Count > 64) throw new InvalidDataException("Preview diagnostic quota exceeded.");
        return Array.AsReadOnly(values.Select(d => d with { Arguments = d.Arguments.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) }).ToArray());
    }
}
