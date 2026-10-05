namespace FolderRewind.Plugin.Abstractions;

/// <summary>A read-only, kind-owned raster preview. Never supplies UI objects or mutation commands.</summary>
public interface ISpatialPreviewCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context);
    ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context);
    ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, PluginInvocationContext context);
}

/// <summary>Generation identifies a browsing snapshot, not an atomic filesystem snapshot. Refresh starts a new generation.</summary>
public sealed record SpatialPreviewSource(ConfigSnapshot Config, FolderSnapshot Folder, Guid Generation);
public sealed record SpatialPreviewBounds(double MinX, double MinY, double MaxX, double MaxY);
public sealed record SpatialPreviewLayer(string Id, LocalizedText Name, SpatialPreviewBounds Bounds,
    double MinorGridSize, double MajorGridSize, SpatialPreviewHeight? Height);
public sealed record SpatialPreviewHeight(int Minimum, int Maximum, int DefaultValue, LocalizedText Label);
public sealed record SpatialPreviewDescription(IReadOnlyList<SpatialPreviewLayer> Layers,
    LocalizedText HorizontalAxis, LocalizedText VerticalAxis, IReadOnlyList<PluginDiagnostic> Diagnostics);

/// <summary>Square world tile: origin and units-per-pixel are in provider coordinates. PixelSize is 256.</summary>
public sealed record SpatialPreviewTileRequest(SpatialPreviewSource Source, string LayerId,
    double OriginX, double OriginY, double UnitsPerPixel, int? MaximumHeight);
public sealed record SpatialPreviewTile(int Width, int Height, ReadOnlyMemory<byte> BgraPremultiplied,
    IReadOnlyList<PluginDiagnostic> Diagnostics);
public sealed record SpatialPreviewPointRequest(SpatialPreviewSource Source, string LayerId,
    double X, double Y, int? MaximumHeight);
public sealed record SpatialPreviewPoint(IReadOnlyList<FolderMetadataField> Fields,
    IReadOnlyList<PluginDiagnostic> Diagnostics);
