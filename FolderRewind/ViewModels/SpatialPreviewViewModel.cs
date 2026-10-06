using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record PreviewLayerChoice(SpatialPreviewLayer Layer)
{
    public string Name => PluginV3SpatialPreview.Localize(Layer.Name);
}

public sealed record PreviewTargetChoice(SpatialPreviewTarget Target, string LayerName)
{
    public string Name => PluginV3SpatialPreview.Localize(Target.Name);
    public string Description => $"{PluginV3SpatialPreview.Localize(Target.Group)} · {LayerName} · {Target.X:0}, {Target.Y:0}";
}


public sealed partial class SpatialPreviewViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable
{
    private static readonly PreviewCameraStore Cameras = new();
    public ObservableCollection<PreviewTargetChoice> Targets { get; } = [];
    public SpatialPreviewNavigation? Navigation { get; private set; }
    [ObservableProperty] public partial string LayerLabel { get; set; } = I18n.GetString("Preview_LayerLabel");
    [ObservableProperty] public partial string NavigationLabel { get; set; } = I18n.GetString("Preview_NavigationLabel");
    [ObservableProperty] public partial string CellLabel { get; set; } = I18n.GetString("Preview_CellLabel");
    [ObservableProperty] public partial string CursorText { get; set; } = "";
    [ObservableProperty] public partial string NavigationStatus { get; set; } = "";
    private readonly PreviewRequestEpoch _epoch = new();
    private PluginV3SpatialPreview? _opening;
    internal PluginV3SpatialPreview? Session { get; private set; }
    public ObservableCollection<PreviewLayerChoice> Layers { get; } = [];
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial string Details { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string HorizontalAxis { get; set; }
    [ObservableProperty] public partial string VerticalAxis { get; set; }
    public SpatialPreviewViewModel() { Title = Status = Details = ""; HorizontalAxis = "X"; VerticalAxis = "Y"; }

    public async Task LoadAsync(SpatialPreviewNavigationParameter navigation)
    {
        var revision = _epoch.Advance();
        ReleaseSessions();
        IsLoading = true; Status = I18n.GetString("Preview_Loading"); Details = ""; Layers.Clear(); Session = null;
        try
        {
            var session = _opening = PluginV3SpatialPreview.Open(navigation.ConfigId, navigation.FolderId);
            Title = session.Source.Folder.DisplayName;
            var description = await session.DescribeAsync(_epoch.Token);
            if (!_epoch.IsCurrent(revision) || !session.IsCurrent()) return;
            Session = session; _opening = null;
            LayerLabel = description.LayerLabel is { } label ? PluginV3SpatialPreview.Localize(label) : I18n.GetString("Preview_LayerLabel");
            NavigationLabel = description.NavigationLabel is { } nav ? PluginV3SpatialPreview.Localize(nav) : I18n.GetString("Preview_NavigationLabel");
            CellLabel = description.CellLabel is { } cell ? PluginV3SpatialPreview.Localize(cell) : I18n.GetString("Preview_CellLabel");
            HorizontalAxis = PluginV3SpatialPreview.Localize(description.HorizontalAxis);
            VerticalAxis = PluginV3SpatialPreview.Localize(description.VerticalAxis);
            foreach (var layer in description.Layers) Layers.Add(new(layer));
            Status = I18n.GetString(Layers.Count == 0 ? "Preview_Empty" : "Preview_ReadOnly");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_epoch.IsCurrent(revision)) Status = I18n.GetString("Preview_Failed"); LogService.LogWarning(ex.ToString(), "SpatialPreview"); }
        finally
        {
            if (_epoch.IsCurrent(revision))
            {
                IsLoading = false;
                if (Session is null) ReleaseSessions();
            }
        }
    }
    public async Task LoadNavigationAsync()
    {
        if (Session is not { } session) return;
        var revision = _epoch.Revision; NavigationStatus = I18n.GetString("Preview_Loading");
        try
        {
            var navigation = await session.GetNavigationTargetsAsync(_epoch.Token);
            if (!_epoch.IsCurrent(revision) || !ReferenceEquals(Session, session)) return;
            Navigation = navigation;
            foreach (var target in navigation.Targets) Targets.Add(new(target, Layers.FirstOrDefault(l => l.Layer.Id == target.LayerId)?.Name ?? target.LayerId));
            NavigationStatus = Targets.Count == 0 ? I18n.GetString("Preview_NoTargets") : I18n.GetString("Preview_SavedPosition");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_epoch.IsCurrent(revision)) NavigationStatus = I18n.GetString("Preview_Failed"); LogService.LogWarning(ex.ToString(), "SpatialPreview"); }
    }
    public void SaveCamera(string layer, double x, double y, double scale, int? height)
    {
        if (Session is not { } session) return;
        Cameras.Save(new(session.Source.Config.ConfigId, session.Source.Folder.FolderId, session.Source.Folder.Path),
            new(layer, x, y, scale, height));
    }
    public PreviewCameraState? SavedCamera(string? layer = null)
    {
        if (Session is not { } session) return null;
        return Cameras.Get(new(session.Source.Config.ConfigId, session.Source.Folder.FolderId, session.Source.Folder.Path), layer);
    }
    public void UpdateCursor(double x, double y, SpatialPreviewLayer layer)
        => CursorText = $"{HorizontalAxis}: {Math.Floor(x):N0}  {VerticalAxis}: {Math.Floor(y):N0} · {CellLabel}: {Math.Floor(x / layer.MinorGridSize):N0}, {Math.Floor(y / layer.MinorGridSize):N0}";
    public void Invalidate()
    {
        _epoch.Advance(); ReleaseSessions(); Layers.Clear(); IsLoading = false;
        Status = I18n.GetString("Preview_SourceChanged");
    }
    private void ReleaseSessions()
    {
        if (_opening is { } opening) TaskObserver.Observe(opening.CloseAsync(), nameof(SpatialPreviewViewModel));
        if (Session is { } session) TaskObserver.Observe(session.CloseAsync(), nameof(SpatialPreviewViewModel));
        _opening = null; Session = null; Targets.Clear(); Navigation = null; NavigationStatus = "";
    }
    public void Dispose() { _epoch.Dispose(); ReleaseSessions(); Layers.Clear(); Details = ""; }

}
