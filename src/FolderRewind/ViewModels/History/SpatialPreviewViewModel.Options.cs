using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.ViewModels;

public sealed record PreviewModeChoice(SpatialPreviewMode Mode)
{
    public string Name => PluginV3SpatialPreview.Localize(Mode.Name);
}

public sealed partial class SpatialPreviewViewModel
{
    public ObservableCollection<PreviewModeChoice> Modes { get; } = [];
    public PreviewModeChoice? CurrentMode { get; private set; }
    [ObservableProperty] public partial bool SupportsStatusOverlay { get; set; }
    [ObservableProperty] public partial bool ShowStatusOverlay { get; set; }
    [ObservableProperty] public partial string DistanceUnit { get; set; } = I18n.GetString("Preview_DefaultUnit");
    [ObservableProperty] public partial string LegendText { get; set; } = "";
    [ObservableProperty] public partial string StatisticsText { get; set; } = "";
    [ObservableProperty] public partial string SourceLabel { get; set; } = I18n.GetString("Preview_CurrentDirectory");

    public PreviewModeChoice? ConfigureModes(SpatialPreviewLayer layer)
    {
        Modes.Clear();
        foreach (var mode in layer.ViewModes) Modes.Add(new(mode));
        var saved = SavedCamera(layer.Id);
        return Modes.FirstOrDefault(m => m.Mode.Id == saved?.ViewModeId)
            ?? Modes.FirstOrDefault(m => m.Mode.Id == layer.DefaultViewModeId) ?? Modes.FirstOrDefault();
    }

    public void SelectMode(SpatialPreviewLayer layer, PreviewModeChoice? choice)
    {
        CurrentMode = choice;
        var saved = SavedCamera(layer.Id, choice?.Mode.Id);
        var height = choice?.Mode.Height ?? layer.Height;
        int? value = saved?.Height;
        if (value is null && choice?.Mode.UseNavigationHeightAsDefault == true && Navigation?.QuickTargetId is { } id)
            value = Targets.FirstOrDefault(t => t.Target.Id == id && t.Target.LayerId == layer.Id)?.Target.SuggestedHeight;
        ConfigureHeight(height, value);
    }

    public void ApplySuggestedHeight(int height)
    {
        if (!HeightEnabled || CurrentMode?.Mode.UseNavigationHeight != true) return;
        HeightText = System.Math.Clamp(height, (int)HeightMinimum, (int)HeightMaximum).ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    public void SetStatistics(SpatialPreviewStatistics value)
        => StatisticsText = I18n.Format("Preview_LoadedStatistics", value.Readable, value.Pending, value.NoData,
            value.NoVisibleSurface, value.Unsupported, value.Failed, value.MissingColor);
}
