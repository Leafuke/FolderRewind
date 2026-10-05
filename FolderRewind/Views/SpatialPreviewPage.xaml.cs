using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class SpatialPreviewPage : Page
{
    public SpatialPreviewViewModel ViewModel { get; } = new();
    private SpatialPreviewNavigationParameter? _navigation;
    private PreviewLayerChoice? _currentLayer;
    private bool _settingHeight;
    private bool _active;
    private long _loadRevision;
    private long _interactionRevision;
    public SpatialPreviewPage()
    {
        InitializeComponent();
        Map.StatusChanged += text => ViewModel.Status = text;
        Map.DetailsChanged += text => { ViewModel.Details = text; DetailLayout.IsPaneOpen = true; };
        Map.Interacted += () => _interactionRevision++;
        Map.PositionChanged += (x, y) => { if (_currentLayer is { } layer) ViewModel.UpdateCursor(x, y, layer.Layer); };
        Map.CursorChanged += (x, y) => { if (_currentLayer is { } layer) ViewModel.UpdateCursor(x, y, layer.Layer); };
        SizeChanged += (_, e) => DetailLayout.DisplayMode = e.NewSize.Width < 900 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        AutomationProperties.SetName(ZoomInButton, I18n.GetString("Preview_ZoomInName"));
        AutomationProperties.SetName(ZoomOutButton, I18n.GetString("Preview_ZoomOutName"));
        ToolTipService.SetToolTip(ZoomInButton, I18n.GetString("Preview_ZoomInName"));
        ToolTipService.SetToolTip(ZoomOutButton, I18n.GetString("Preview_ZoomOutName"));
        ToolTipService.SetToolTip(NavigateButton, I18n.GetString("Preview_SavedPosition"));
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
        SaveView(); _active = false; _loadRevision++;
        ConfigService.Saved -= OnConfigSaved;
        Map.Clear(); _currentLayer = null; ViewModel.Dispose(); base.OnNavigatedFrom(e);
    }
    private void SaveView()
    {
        if (_currentLayer is not { } layer) return;
        var camera = Map.Camera;
        ViewModel.SaveCamera(layer.Layer.Id, camera.X, camera.Y, camera.Scale,
            layer.Layer.Height is not null && double.IsFinite(MaximumHeight.Value) ? (int)MaximumHeight.Value : null);
    }
    private async Task LoadAsync()
    {
        var revision = ++_loadRevision;
        SaveView(); _currentLayer = null; Map.Clear(); DetailLayout.IsPaneOpen = false;
        if (_navigation is null) return;
        await ViewModel.LoadAsync(_navigation);
        if (!_active || revision != _loadRevision || ViewModel.Layers.Count == 0) return;
        var saved = ViewModel.SavedCamera();
        Layers.SelectedItem = ViewModel.Layers.FirstOrDefault(l => l.Layer.Id == saved?.LayerId) ?? ViewModel.Layers[0];
        var interaction = _interactionRevision;
        await ViewModel.LoadNavigationAsync();
        if (!_active || revision != _loadRevision || saved is not null || interaction != _interactionRevision) return;
        if (ViewModel.Navigation?.DefaultTargetId is { } id && ViewModel.Targets.FirstOrDefault(t => t.Target.Id == id) is { } target)
            Navigate(target.Target);
    }
    private void OnConfigSaved() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_active && ViewModel.Session is { } session && !session.IsCurrent())
        { SaveView(); Map.Clear(); _currentLayer = null; ViewModel.Invalidate(); DetailLayout.IsPaneOpen = false; }
    });
    private void OnLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveView(); _interactionRevision++;
        if (Layers.SelectedItem is not PreviewLayerChoice choice) { _currentLayer = null; Map.Clear(); return; }
        _currentLayer = choice; _settingHeight = true;
        var height = choice.Layer.Height; var saved = ViewModel.SavedCamera(choice.Layer.Id);
        MaximumHeight.IsEnabled = height is not null;
        MaximumHeight.Minimum = height?.Minimum ?? 0; MaximumHeight.Maximum = height?.Maximum ?? 1;
        MaximumHeight.Value = height is null ? 0 : Math.Clamp(saved?.Height ?? height.DefaultValue, height.Minimum, height.Maximum);
        _settingHeight = false; ViewModel.Details = ""; DetailLayout.IsPaneOpen = false;
        Map.SetSource(ViewModel.Session, choice.Layer, height is null ? null : (int)MaximumHeight.Value, true);
        if (saved is not null) Map.RestoreCamera(saved.X, saved.Y, saved.Scale);
    }
    private void OnHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_settingHeight || !double.IsFinite(args.NewValue) || _currentLayer is not { } choice) return;
        ViewModel.Details = ""; DetailLayout.IsPaneOpen = false; _interactionRevision++;
        Map.SetSource(ViewModel.Session, choice.Layer, (int)args.NewValue, false);
    }
    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.Zoom(2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.Zoom(.5);
    private void OnFit(object sender, RoutedEventArgs e) => Map.Fit();
    private void OnGridToggled(object sender, RoutedEventArgs e) { if (Map is not null) Map.ShowGrid = GridToggle.IsOn; }
    private void OnCloseDetails(object sender, RoutedEventArgs e) { DetailLayout.IsPaneOpen = false; Map.Focus(FocusState.Programmatic); }
    private void OnCoordinateOpening(object sender, object e)
    { var camera = Map.Camera; CoordinateX.Value = Math.Floor(camera.X); CoordinateY.Value = Math.Floor(camera.Y); }
    private void OnLocate(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CoordinateX.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var x)
            && double.TryParse(CoordinateY.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var y))
        { Map.Locate(x, y); CoordinateFlyout.Hide(); }
    }
    private void OnQuickNavigate(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (ViewModel.Navigation?.QuickTargetId is { } id && ViewModel.Targets.FirstOrDefault(t => t.Target.Id == id) is { } target) Navigate(target.Target);
        else NavigationFlyout.ShowAt(NavigateButton);
    }
    private void OnTargetClicked(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is PreviewTargetChoice target) Navigate(target.Target); }
    private void Navigate(SpatialPreviewTarget target)
    {
        var layer = ViewModel.Layers.FirstOrDefault(l => l.Layer.Id == target.LayerId);
        if (layer is null) { ViewModel.NavigationStatus = I18n.GetString("Preview_TargetUnavailable"); return; }
        Layers.SelectedItem = layer; Map.Locate(target.X, target.Y); NavigationFlyout.Hide();
    }
    private void OnRefresh(object sender, RoutedEventArgs e) => TaskObserver.Observe(LoadAsync(), nameof(SpatialPreviewPage));
}
