using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record PreviewLayerChoice(SpatialPreviewLayer Layer)
{
    public string Name => PluginV3SpatialPreview.Localize(Layer.Name);
}

public sealed partial class SpatialPreviewViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable
{
    private readonly PreviewRequestEpoch _epoch = new();
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
        IsLoading = true; Status = I18n.GetString("Preview_Loading"); Details = ""; Layers.Clear(); Session = null;
        try
        {
            var session = PluginV3SpatialPreview.Open(navigation.ConfigId, navigation.FolderId);
            Title = session.Source.Folder.DisplayName;
            var description = await session.DescribeAsync(_epoch.Token);
            if (!_epoch.IsCurrent(revision) || !session.IsCurrent()) return;
            Session = session;
            HorizontalAxis = PluginV3SpatialPreview.Localize(description.HorizontalAxis);
            VerticalAxis = PluginV3SpatialPreview.Localize(description.VerticalAxis);
            foreach (var layer in description.Layers) Layers.Add(new(layer));
            Status = I18n.GetString(Layers.Count == 0 ? "Preview_Empty" : "Preview_ReadOnly");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_epoch.IsCurrent(revision)) Status = I18n.GetString("Preview_Failed") + " " + ex.Message; }
        finally { if (_epoch.IsCurrent(revision)) IsLoading = false; }
    }
    public void Invalidate()
    {
        _epoch.Advance(); Session = null; Layers.Clear(); IsLoading = false;
        Status = I18n.GetString("Preview_SourceChanged");
    }
    public void Dispose() { _epoch.Dispose(); Session = null; }
}
