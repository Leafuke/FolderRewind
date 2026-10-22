using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record PreviewLayerChoice(SpatialPreviewLayer Layer)
{
    public string Name => PluginV3SpatialPreview.Localize(Layer.Name);
}

public sealed record PreviewTargetChoice(SpatialPreviewTarget Target, string LayerName)
{
    public string Name => PluginV3SpatialPreview.Localize(Target.Name);
    public string Description => $"{PluginV3SpatialPreview.Localize(Target.Group)} · {LayerName} · {Target.X:0}, {Target.Y:0}" + (Target.SuggestedHeight is { } height ? " · " + I18n.Format("Preview_TargetHeight", height) : "");
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
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private HistoricalPreviewWorkspace? _history;
    private bool _disposed;
    [ObservableProperty] public partial bool IsPreparing { get; set; }
    [ObservableProperty] public partial bool NeedsHistoryDownload { get; set; }
    [ObservableProperty] public partial bool HasLoadError { get; set; }
    [ObservableProperty] public partial string ErrorDetails { get; set; } = "";
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
        if (_disposed) return;
        var revision = _epoch.Advance();
        var token = _epoch.Token;
        var entered = false;
        IsLoading = true; IsPreparing = false; NeedsHistoryDownload = HasLoadError = false;
        Status = I18n.GetString("Preview_Loading"); Details = ErrorDetails = ""; Layers.Clear();
        ConfigureHeight(null, null); StatisticsText = LegendText = "";
        try
        {
            // Serialize preparations and disposal; a cancelled materializer must finish before its tree can be reclaimed.
            await _loadGate.WaitAsync(token); entered = true;
            await ReleaseSessionsAsync();
            token.ThrowIfCancellationRequested();
            if (_history is { } prior && (navigation.VersionId != prior.Version.VersionId.Value
                || navigation.ConfigId != prior.Origin.ConfigId || navigation.FolderId != prior.FolderId
                || !PluginV3SpatialPreview.IsCurrent(prior.Origin, prior.FolderId)))
                await ReleaseHistoryAsync();
            if (navigation.VersionId is { } versionId && _history is null)
            {
                IsPreparing = true;
                SourceLabel = I18n.GetString("Preview_HistoryPreparing");
                var config = ConfigSnapshotMapper.Copy(ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == navigation.ConfigId));
                Title = config.SourceFolders.Single(f => Guid.TryParse(f.Id, out var id) && id == navigation.FolderId).DisplayName;
                var progress = new Progress<HistoricalPreviewStage>(stage =>
                {
                    if (_epoch.IsCurrent(revision) && IsPreparing) Status = I18n.GetString("Preview_History" + stage);
                });
                var prepared = await Task.Run(() => NativeHistoryApplicationService.PrepareWorldPreviewAsync(config,
                    navigation.FolderId, new(versionId), progress, token), token);
                if (!_epoch.IsCurrent(revision)) { await prepared.DisposeAsync(); return; }
                _history = prepared;
                IsPreparing = false;
            }
            token.ThrowIfCancellationRequested();
            var session = _opening = _history is { } history ? PluginV3SpatialPreview.Open(history)
                : PluginV3SpatialPreview.Open(navigation.ConfigId, navigation.FolderId);
            Title = session.Source.Folder.DisplayName;
            SourceLabel = _history is { } historical
                ? I18n.Format("Preview_HistorySource", UserDisplayFormatter.Date(historical.Version.CreatedAtUtc.ToLocalTime()),
                    UserDisplayFormatter.LongTime(historical.Version.CreatedAtUtc.ToLocalTime()), historical.Version.VersionId.ToString()[..8])
                    + (historical.IsPartial ? " · " + I18n.GetString("Preview_HistoryPartial") : "")
                : I18n.Format("Preview_CurrentPath", session.OriginalPath);
            var description = await session.DescribeAsync(token);
            if (!_epoch.IsCurrent(revision) || !session.IsCurrent()) return;
            Session = session; _opening = null;
            LayerLabel = description.LayerLabel is { } label ? PluginV3SpatialPreview.Localize(label) : I18n.GetString("Preview_LayerLabel");
            NavigationLabel = description.NavigationLabel is { } nav ? PluginV3SpatialPreview.Localize(nav) : I18n.GetString("Preview_NavigationLabel");
            CellLabel = description.CellLabel is { } cell ? PluginV3SpatialPreview.Localize(cell) : I18n.GetString("Preview_CellLabel");
            HorizontalAxis = PluginV3SpatialPreview.Localize(description.HorizontalAxis);
            VerticalAxis = PluginV3SpatialPreview.Localize(description.VerticalAxis);
            DistanceUnit = description.DistanceUnit is { } unit ? PluginV3SpatialPreview.Localize(unit) : I18n.GetString("Preview_DefaultUnit");
            SupportsStatusOverlay = description.SupportsStatusOverlay;
            LegendText = string.Join(" · ", description.Legend.Select(l => PluginV3SpatialPreview.Localize(l.Label)));
            foreach (var layer in description.Layers) Layers.Add(new(layer));
            Status = I18n.GetString(Layers.Count == 0 ? "Preview_Empty" : _history is null ? "Preview_ReadOnly" : "Preview_HistoryReadOnly");
            if (description.Diagnostics.Count > 0)
            {
                ErrorDetails = string.Join(Environment.NewLine, description.Diagnostics.Select(d => I18n.PickBest(d.Arguments, d.Code) ?? d.Code));
                HasLoadError = true;
                if (Layers.Count == 0) Status = ErrorDetails;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_epoch.IsCurrent(revision))
            {
                NeedsHistoryDownload = ex is HistoricalPreviewUnavailableException { RequiresDownload: true };
                Status = ex is HistoricalPreviewUnavailableException ? ex.Message : I18n.GetString("Preview_Failed");
                ErrorDetails = ex.Message; HasLoadError = true;
            }
            LogService.LogWarning(ex.ToString(), "SpatialPreview");
        }
        finally
        {
            try { if (entered && Session is null) await ReleaseSessionsAsync(); }
            finally { if (entered) _loadGate.Release(); }
            if (_epoch.IsCurrent(revision)) { IsLoading = IsPreparing = false; }
        }
    }
    public void CancelPreparation()
    {
        if (_disposed || !IsPreparing) return;
        _epoch.Advance(); IsLoading = IsPreparing = false;
        Status = I18n.GetString("Preview_HistoryCancelled");
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
        Cameras.Save(new(session.Source.Config.ConfigId, session.Source.Folder.FolderId, session.CameraIdentity),
            new(layer, x, y, scale, height) { ViewModeId = CurrentMode?.Mode.Id, ShowStatusOverlay = ShowStatusOverlay });
    }
    public PreviewCameraState? SavedCamera(string? layer = null, string? mode = null)
    {
        if (Session is not { } session) return null;
        return Cameras.Get(new(session.Source.Config.ConfigId, session.Source.Folder.FolderId, session.CameraIdentity), layer, mode);
    }
    public void UpdateCursor(double x, double y, SpatialPreviewLayer layer)
        => CursorText = $"{HorizontalAxis}: {Math.Floor(x):N0}  {VerticalAxis}: {Math.Floor(y):N0} · {CellLabel}: {Math.Floor(x / layer.MinorGridSize):N0}, {Math.Floor(y / layer.MinorGridSize):N0}";
    public void Invalidate()
    {
        if (_disposed) return;
        var revision = _epoch.Advance(); Layers.Clear(); IsLoading = IsPreparing = false;
        Status = I18n.GetString("Preview_SourceChanged");
        TaskObserver.Observe(ReleaseAllAsync(revision), nameof(SpatialPreviewViewModel));
    }
    private async Task ReleaseSessionsAsync()
    {
        var opening = _opening; var session = Session;
        _opening = null; Session = null; Targets.Clear(); Navigation = null; NavigationStatus = "";
        var calls = new List<Task>();
        if (opening is not null) calls.Add(opening.CloseAsync());
        if (session is not null) calls.Add(session.CloseAsync());
        await Task.WhenAll(calls);
    }
    private async Task ReleaseHistoryAsync()
    {
        var history = _history; _history = null;
        if (history is not null) await history.DisposeAsync();
    }
    private async Task ReleaseAllAsync(long? revision = null)
    {
        await _loadGate.WaitAsync();
        try
        {
            if (revision is { } expected && !_epoch.IsCurrent(expected)) return;
            try { await ReleaseSessionsAsync(); }
            finally { await ReleaseHistoryAsync(); }
        }
        finally { _loadGate.Release(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _epoch.Dispose(); Layers.Clear(); Details = "";
        TaskObserver.Observe(ReleaseAllAsync(), nameof(SpatialPreviewViewModel));
    }
}
