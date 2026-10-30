using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.Services;

namespace FolderRewind.ViewModels;

public sealed partial class SpatialPreviewViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsMapLoading { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreviewWarning)), NotifyPropertyChangedFor(nameof(InformationName))]
    public partial bool HasMapWarnings { get; set; }
    [ObservableProperty] public partial bool IsHistoricalSource { get; set; }
    [ObservableProperty] public partial bool IsPartialSource { get; set; }
    [ObservableProperty] public partial bool IsFocusMode { get; set; }

    public bool HasLayers => Layers.Count > 0;
    public bool ShowMapMessage => IsPreparing || (!IsLoading && !HasLayers);
    public bool IsBusy => IsLoading || IsMapLoading;
    public bool HasPreviewWarning => HasLoadError || HasMapWarnings;
    public string InformationName => I18n.GetString(HasPreviewWarning ? "Preview_InformationWarning" : "Preview_InformationName");
    public string ViewButtonText => string.IsNullOrEmpty(HeightSummary) ? I18n.GetString("Preview_DefaultViewMode") : HeightSummary;

    public void SetMapActivity(bool loading, bool hasWarnings)
    {
        IsMapLoading = loading;
        HasMapWarnings = hasWarnings;
    }
}
