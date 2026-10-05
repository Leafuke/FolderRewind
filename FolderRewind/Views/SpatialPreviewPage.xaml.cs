using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class SpatialPreviewPage : Page
{
    public SpatialPreviewViewModel ViewModel { get; } = new();
    private SpatialPreviewNavigationParameter? _navigation;
    private bool _settingHeight;
    private bool _active;
    public SpatialPreviewPage()
    {
        InitializeComponent();
        Map.StatusChanged += text => ViewModel.Status = text;
        Map.DetailsChanged += text => ViewModel.Details = text;
        Map.PositionChanged += (x, y) => { CoordinateX.Value = Math.Floor(x); CoordinateY.Value = Math.Floor(y); };
        SizeChanged += (_, e) => DetailColumn.Width = new GridLength(e.NewSize.Width < 850 ? 180 : 240);
    }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        _navigation = e.Parameter as SpatialPreviewNavigationParameter;
        ConfigService.Saved += OnConfigSaved;
        TaskObserver.Observe(LoadAsync(), nameof(SpatialPreviewPage));
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false; ConfigService.Saved -= OnConfigSaved;
        Map.Clear(); ViewModel.Dispose(); base.OnNavigatedFrom(e);
    }
    private async Task LoadAsync()
    {
        Map.Clear();
        if (_navigation is null) return;
        await ViewModel.LoadAsync(_navigation);
        if (_active && ViewModel.Layers.Count > 0) Layers.SelectedIndex = 0;
    }
    private void OnConfigSaved() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_active && ViewModel.Session is { } session && !session.IsCurrent()) { Map.Clear(); ViewModel.Invalidate(); }
    });
    private void OnLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Layers.SelectedItem is not PreviewLayerChoice choice) { Map.Clear(); return; }
        _settingHeight = true;
        var height = choice.Layer.Height;
        MaximumHeight.IsEnabled = height is not null;
        MaximumHeight.Header = height is null ? "" : PluginV3SpatialPreview.Localize(height.Label);
        AutomationProperties.SetName(MaximumHeight, MaximumHeight.Header?.ToString() ?? "");
        MaximumHeight.Minimum = height?.Minimum ?? 0; MaximumHeight.Maximum = height?.Maximum ?? 1;
        MaximumHeight.Value = height?.DefaultValue ?? 0;
        _settingHeight = false; ViewModel.Details = "";
        Map.SetSource(ViewModel.Session, choice.Layer, height?.DefaultValue, true);
    }
    private void OnHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_settingHeight || !double.IsFinite(args.NewValue) || Layers.SelectedItem is not PreviewLayerChoice choice) return;
        Map.SetSource(ViewModel.Session, choice.Layer, (int)args.NewValue, false);
    }
    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.Zoom(2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.Zoom(.5);
    private void OnLocate(object sender, RoutedEventArgs e)
    {
        // UIA invocation and keyboard activation need not move focus out of NumberBox first.
        if (double.TryParse(CoordinateX.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var x)
            && double.TryParse(CoordinateY.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var y))
            Map.Locate(x, y);
    }
    private void OnRefresh(object sender, RoutedEventArgs e) => TaskObserver.Observe(LoadAsync(), nameof(SpatialPreviewPage));
    private void OnBack(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("Manager", _navigation is null ? null : ManagerNavigationParameter.ForConfig(_navigation.ConfigId));
}
