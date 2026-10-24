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
            if (l.CoordinateBounds is { } b && (!Finite(b.MinX, b.MinY, b.MaxX, b.MaxY)
                || b.MinX >= b.MaxX || b.MinY >= b.MaxY))
                throw new InvalidDataException("Invalid preview coordinate bounds.");
            if (l.ViewModes.Count > 16 || l.ViewModes.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != l.ViewModes.Count
                || l.DefaultViewModeId is { } defaultMode && !l.ViewModes.Any(m => m.Id == defaultMode))
                throw new InvalidDataException("Invalid preview modes.");
            var modes = l.ViewModes.Select(m =>
            {
                if (string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 128) throw new InvalidDataException("Invalid preview mode.");
                return m with { Name = Freeze(m.Name), Height = FreezeHeight(m.Height) };
            }).ToArray();
            return l with { Name = Freeze(l.Name), Height = FreezeHeight(l.Height), ViewModes = Array.AsReadOnly(modes) };
        }).ToArray();
        return new(Array.AsReadOnly(layers), Freeze(value.HorizontalAxis), Freeze(value.VerticalAxis), Freeze(value.Diagnostics))
        { LayerLabel = value.LayerLabel is { } label ? Freeze(label) : null,
          NavigationLabel = value.NavigationLabel is { } navigation ? Freeze(navigation) : null,
          CellLabel = value.CellLabel is { } cell ? Freeze(cell) : null,
          DistanceUnit = value.DistanceUnit is { } unit ? Freeze(unit) : null,
          SupportsStatusOverlay = value.SupportsStatusOverlay,
          Legend = FreezeLegend(value.Legend), Worlds = FreezeWorlds(value.Worlds) };
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
        ValidateMode(request.ViewModeId);
        using var lease = Acquire(request.Source, token);
        var value = await lease.Capability.RenderAsync(request, lease.Context).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return FreezeTile(value);
    }

    public async ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, CancellationToken token)
    {
        if (!Finite(request.X, request.Y)) throw new ArgumentException("Invalid preview point.");
        ValidateMode(request.ViewModeId);
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
        if (!Enum.IsDefined(value.ContentState) || value.Statistics is { } stats && new[] { stats.Readable, stats.Pending,
            stats.NoData, stats.NoVisibleSurface, stats.Unsupported, stats.Failed, stats.MissingColor }.Any(v => v < 0))
            throw new InvalidDataException("Invalid preview content state or statistics.");
        return new(TileSize, TileSize, value.BgraPremultiplied.ToArray(), Freeze(value.Diagnostics))
            { IsFinal = value.IsFinal, ContentState = value.ContentState, Statistics = value.Statistics };
    }

    private PluginCapabilityLease<ISpatialPreviewCapability> Acquire(SpatialPreviewSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Generation == Guid.Empty || !source.Config.Folders.Any(f => f.FolderId == source.Folder.FolderId && f.Path == source.Folder.Path))
            throw new ArgumentException("Preview source does not belong to the configuration snapshot.");
        if (source.WorldId?.Length > 4096 || source.CacheBudgetBytes is < 1048576 or > 134217728)
            throw new ArgumentException("Invalid spatial input bounds or cache budget.");
        var lease = runtime.TryAcquire<ISpatialPreviewCapability>(new(source.Config.Kind.OwnerId.Value), c => c.Kind == source.Config.Kind, token)
            ?? throw new InvalidOperationException("SpatialPreviewUnavailable");
        if (source.FixedInput is { } input && (lease.Capability is not IFixedSpatialPreviewCapability
            || string.IsNullOrWhiteSpace(input.Identity) || string.IsNullOrWhiteSpace(input.ContentDigest)
            || !Path.IsPathFullyQualified(input.RootPath)))
        {
            lease.Dispose();
            throw new InvalidOperationException("FixedSpatialPreviewUnavailable");
        }
        return lease;
    }

    private static IReadOnlyList<SpatialWorld> FreezeWorlds(IReadOnlyList<SpatialWorld> worlds)
    {
        if (worlds.Count > 4096 || worlds.Any(w => string.IsNullOrWhiteSpace(w.Id) || w.Id.Length > 4096)
            || worlds.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != worlds.Count)
            throw new InvalidDataException("Invalid spatial world inventory.");
        return Array.AsReadOnly(worlds.Select(w => w with { Name = Freeze(w.Name) }).ToArray());
    }

    private static void ValidateMode(string? value)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128))
            throw new ArgumentException("Invalid preview view mode.");
    }
    private static SpatialPreviewHeight? FreezeHeight(SpatialPreviewHeight? value)
    {
        if (value is null) return null;
        if (value.Minimum > value.DefaultValue || value.DefaultValue > value.Maximum) throw new InvalidDataException("Invalid preview height.");
        return value with { Label = Freeze(value.Label) };
    }
    private static IReadOnlyList<SpatialPreviewLegendItem> FreezeLegend(IReadOnlyList<SpatialPreviewLegendItem> values)
    {
        if (values.Count > 32 || values.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != values.Count
            || values.Any(v => string.IsNullOrWhiteSpace(v.Id) || v.Id.Length > 128)) throw new InvalidDataException("Invalid preview legend.");
        return Array.AsReadOnly(values.Select(v => v with { Label = Freeze(v.Label) }).ToArray());
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
