namespace FolderRewind.Plugin.Abstractions;

/// <summary>A read-only, kind-owned raster preview. Never supplies UI objects or mutation commands.</summary>
public interface ISpatialPreviewCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context);
    ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context);
    ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, PluginInvocationContext context);
    ValueTask<SpatialPreviewNavigation> GetNavigationTargetsAsync(SpatialPreviewSource source, PluginInvocationContext context)
        => ValueTask.FromResult(new SpatialPreviewNavigation(Array.Empty<SpatialPreviewTarget>(), null));
    /// <summary>Idempotently releases this generation. Cleanup must not depend on the load cancellation token.</summary>
    ValueTask CloseAsync(SpatialPreviewSource source, PluginInvocationContext context) => ValueTask.CompletedTask;
}

/// <summary>Generation identifies a browsing snapshot, not an atomic filesystem snapshot. Refresh starts a new generation.</summary>
public sealed record SpatialPreviewSource(ConfigSnapshot Config, FolderSnapshot Folder, Guid Generation)
{
    public bool IsPartial { get; init; }
    public SpatialFixedInput? FixedInput { get; init; }
    public string? WorldId { get; init; }
    /// <summary>Host ceiling for a generation's decoded/raster cache. Multiple panels divide one workspace budget.</summary>
    public long CacheBudgetBytes { get; init; } = 128L * 1024 * 1024;
}
public sealed record SpatialPreviewBounds(double MinX, double MinY, double MaxX, double MaxY);
public sealed record SpatialPreviewLayer(string Id, LocalizedText Name, SpatialPreviewBounds Bounds,
    double MinorGridSize, double MajorGridSize, SpatialPreviewHeight? Height)
{
    /// <summary>Legal navigation coordinates, independent of the generated data bounds.</summary>
    public SpatialPreviewBounds? CoordinateBounds { get; init; }
    public IReadOnlyList<SpatialPreviewMode> ViewModes { get; init; } = Array.Empty<SpatialPreviewMode>();
    public string? DefaultViewModeId { get; init; }
}
public sealed record SpatialPreviewHeight(int Minimum, int Maximum, int DefaultValue, LocalizedText Label);
public sealed record SpatialPreviewMode(string Id, LocalizedText Name, SpatialPreviewHeight? Height)
{
    /// <summary>Apply the saved target height when the user explicitly navigates.</summary>
    public bool UseNavigationHeight { get; init; }
    /// <summary>Also prefer the available saved target height for a mode's first/default view.</summary>
    public bool UseNavigationHeightAsDefault { get; init; }
}
public sealed record SpatialPreviewLegendItem(string Id, LocalizedText Label, uint Argb);
public enum SpatialPreviewContentState { Unknown, NoData, NoVisibleSurface, HasData }
public sealed record SpatialPreviewStatistics(int Readable, int Pending, int NoData, int NoVisibleSurface,
    int Unsupported, int Failed, int MissingColor);
public sealed record SpatialPreviewDescription(IReadOnlyList<SpatialPreviewLayer> Layers,
    LocalizedText HorizontalAxis, LocalizedText VerticalAxis, IReadOnlyList<PluginDiagnostic> Diagnostics)
{
    public IReadOnlyList<SpatialWorld> Worlds { get; init; } = Array.Empty<SpatialWorld>();
    public LocalizedText? LayerLabel { get; init; }
    public LocalizedText? NavigationLabel { get; init; }
    public LocalizedText? CellLabel { get; init; }
    public LocalizedText? DistanceUnit { get; init; }
    public bool SupportsStatusOverlay { get; init; }
    public IReadOnlyList<SpatialPreviewLegendItem> Legend { get; init; } = Array.Empty<SpatialPreviewLegendItem>();
}

public sealed record SpatialPreviewTarget(string Id, LocalizedText Group, LocalizedText Name,
    string LayerId, double X, double Y)
{
    public int? SuggestedHeight { get; init; }
}
public sealed record SpatialPreviewNavigation(IReadOnlyList<SpatialPreviewTarget> Targets, string? DefaultTargetId)
{
    public string? QuickTargetId { get; init; }
}

/// <summary>Square world tile: origin and units-per-pixel are in provider coordinates. PixelSize is 256.</summary>
public sealed record SpatialPreviewTileRequest(SpatialPreviewSource Source, string LayerId,
    double OriginX, double OriginY, double UnitsPerPixel, int? MaximumHeight)
{
    public string? ViewModeId { get; init; }
    public bool ShowStatusOverlay { get; init; }
}
public sealed record SpatialPreviewTile(int Width, int Height, ReadOnlyMemory<byte> BgraPremultiplied,
    IReadOnlyList<PluginDiagnostic> Diagnostics)
{
    /// <summary>False requests another bounded refinement call while the tile is visible.</summary>
    public bool IsFinal { get; init; } = true;
    public SpatialPreviewContentState ContentState { get; init; }
    public SpatialPreviewStatistics? Statistics { get; init; }
}
public sealed record SpatialPreviewPointRequest(SpatialPreviewSource Source, string LayerId,
    double X, double Y, int? MaximumHeight)
{
    public string? ViewModeId { get; init; }
}
public sealed record SpatialPreviewPoint(IReadOnlyList<FolderMetadataField> Fields,
    IReadOnlyList<PluginDiagnostic> Diagnostics);
