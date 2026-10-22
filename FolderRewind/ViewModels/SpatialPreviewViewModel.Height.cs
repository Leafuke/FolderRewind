using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Globalization;

namespace FolderRewind.ViewModels;

public sealed partial class SpatialPreviewViewModel
{
    private bool _configuringHeight;
    [ObservableProperty] public partial bool HeightEnabled { get; set; }
    [ObservableProperty] public partial double HeightMinimum { get; set; }
    [ObservableProperty] public partial double HeightMaximum { get; set; } = 1;
    [ObservableProperty] public partial double DraftHeight { get; set; }
    [ObservableProperty] public partial string HeightText { get; set; } = "";
    [ObservableProperty] public partial string HeightLabel { get; set; } = I18n.GetString("Preview_ViewHeight.Header");
    [ObservableProperty] public partial string HeightError { get; set; } = "";
    [ObservableProperty] public partial string HeightSummary { get; set; } = "";
    public int? AppliedHeight { get; private set; }

    public void ConfigureHeight(SpatialPreviewHeight? height, int? saved)
    {
        _configuringHeight = true;
        try
        {
            HeightEnabled = height is not null;
            HeightMinimum = height?.Minimum ?? 0;
            HeightMaximum = height?.Maximum ?? 1;
            HeightLabel = height is null ? I18n.GetString("Preview_ViewHeight.Header") : PluginV3SpatialPreview.Localize(height.Label);
            AppliedHeight = height is null ? null : Math.Clamp(saved ?? height.DefaultValue, height.Minimum, height.Maximum);
            ResetHeightDraft();
        }
        finally { _configuringHeight = false; }
    }

    partial void OnDraftHeightChanged(double value)
    {
        if (_configuringHeight || !double.IsFinite(value)) return;
        HeightText = Math.Clamp((int)Math.Round(value), (int)HeightMinimum, (int)HeightMaximum).ToString(CultureInfo.CurrentCulture);
        HeightError = "";
    }

    public bool CommitHeight()
    {
        if (!HeightEnabled) return false;
        if (!int.TryParse(HeightText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var height)
            || height < HeightMinimum || height > HeightMaximum)
        {
            HeightError = I18n.Format("Preview_InvalidHeight", HeightMinimum, HeightMaximum);
            return false;
        }
        var changed = AppliedHeight != height;
        AppliedHeight = height;
        ResetHeightDraft();
        return changed;
    }

    public void ResetHeightDraft()
    {
        DraftHeight = AppliedHeight ?? 0;
        HeightText = AppliedHeight?.ToString(CultureInfo.CurrentCulture) ?? "";
        HeightError = "";
        HeightSummary = AppliedHeight is { } height ? I18n.Format("Preview_AppliedHeight", height) : "";
    }
}
