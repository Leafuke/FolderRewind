using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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
    private bool _heightDragging;
    private bool _settingOptions;
    private bool _active;
    private long _loadRevision;
    private long _interactionRevision;
    public SpatialPreviewPage()
    {
        InitializeComponent();
        Map.StatusChanged += text => ViewModel.Status = text;
        Map.ActivityChanged += ViewModel.SetMapActivity;
        Map.DetailsChanged += text => { ViewModel.Details = text; DetailLayout.IsPaneOpen = true; };
        Map.StatisticsChanged += ViewModel.SetStatistics;
        Map.Interacted += () => _interactionRevision++;
        Map.PositionChanged += (x, y) => { if (_currentLayer is { } layer) ViewModel.UpdateCursor(x, y, layer.Layer); };
        Map.ScaleChanged += (units, pixels) => { ScaleBar.Width = pixels; ScaleLabel.Text = I18n.Format("Preview_ScaleWithUnit", units, ViewModel.DistanceUnit); };
        Map.CursorChanged += (x, y) => { if (_currentLayer is { } layer) ViewModel.UpdateCursor(x, y, layer.Layer); };
        SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width);
        HeightSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _heightDragging = true), true);
        HeightSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _heightDragging = false; CommitHeight(); }), true);
        HeightSlider.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => { _heightDragging = false; ViewModel.ResetHeightDraft(); }), true);
        HeightSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_heightDragging || !_active) return;
            _heightDragging = false; CommitHeight();
        })), true);
        MaximumHeight.AddHandler(KeyDownEvent, new KeyEventHandler(OnHeightKeyDown), true);
        ToolTipService.SetToolTip(NavigateButton, I18n.GetString("Preview_SavedPosition"));
        ViewModel.PropertyChanged += OnPresentationChanged;
        TrackFlyouts();
        Loaded += (_, _) => UpdateLayout(ActualWidth);
        Unloaded += (_, _) => RestoreNavigationPane();
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
        ViewFlyout.Hide(); NavigationFlyout.Hide(); CoordinateFlyout.Hide(); InformationFlyout.Hide(); MoreFlyout.Hide();
        RestoreNavigationPane(); ViewModel.PropertyChanged -= OnPresentationChanged;
        ConfigService.Saved -= OnConfigSaved;
        Map.Clear(); _currentLayer = null; ViewModel.Dispose(); base.OnNavigatedFrom(e);
    }
    private void SaveView()
    {
        if (_currentLayer is not { } layer) return;
        var camera = Map.Camera;
        ViewModel.SaveCamera(layer.Layer.Id, camera.X, camera.Y, camera.Scale,
            ViewModel.AppliedHeight);
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
            Navigate(target.Target, false);
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
        _currentLayer = choice;
        var saved = ViewModel.SavedCamera(choice.Layer.Id);
        _settingOptions = true;
        try
        {
            var mode = ViewModel.ConfigureModes(choice.Layer);
            ModeSelector.SelectedItem = mode;
            ViewModel.SelectMode(choice.Layer, mode);
            ViewModel.ShowStatusOverlay = ViewModel.SupportsStatusOverlay && saved?.ShowStatusOverlay == true;
            ModeControls.Visibility = ViewModel.Modes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _settingOptions = false; }
        ApplyView(true);
        if (saved is not null) Map.RestoreCamera(saved.X, saved.Y, saved.Scale);
    }
    private void ApplyView(bool reset)
    {
        if (_currentLayer is not { } choice) return;
        ViewModel.Details = ""; DetailLayout.IsPaneOpen = false;
        Map.SetSource(ViewModel.Session, choice.Layer, ViewModel.AppliedHeight, reset,
            ViewModel.CurrentMode?.Mode.Id, ViewModel.ShowStatusOverlay);
    }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingOptions || _currentLayer is not { } choice) return;
        SaveView(); _interactionRevision++;
        ViewModel.SelectMode(choice.Layer, ModeSelector.SelectedItem as PreviewModeChoice);
        ApplyView(false);
    }
    private void OnOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_settingOptions || _currentLayer is null) return;
        ViewModel.ShowStatusOverlay = OverlayToggle.IsChecked; _interactionRevision++;
        ApplyView(false);
    }
    private void CommitHeight()
    {
        if (!_active || _settingOptions || _currentLayer is null || !ViewModel.CommitHeight()) return;
        ViewModel.Details = ""; DetailLayout.IsPaneOpen = false; _interactionRevision++;
        ApplyView(false);
    }
    private void OnHeightLostFocus(object sender, RoutedEventArgs e) { if (!_heightDragging) CommitHeight(); }
    private void OnHeightSliderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { ViewModel.ResetHeightDraft(); ViewFlyout.Hide(); e.Handled = true; }
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown) CommitHeight();
    }
    private void OnHeightKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { CommitHeight(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { ViewModel.ResetHeightDraft(); ViewFlyout.Hide(); e.Handled = true; }
    }
    private void UpdateLayout(double width)
    {
        DetailLayout.DisplayMode = width < 900 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        Layers.Width = width < 560 ? 104 : 124;
        ViewButton.Width = width < 560 ? 128 : 152;
        SetToolbarAction(NavigateButton, OverflowNavigate, width >= 660);
        SetToolbarAction(RefreshButton, OverflowRefresh, width >= 760);
        SetToolbarAction(FitButton, OverflowFit, width >= 840);
    }
    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.Zoom(2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.Zoom(.5);
    private void OnFit(object sender, RoutedEventArgs e) => Map.Fit();
    private void OnGridToggled(object sender, RoutedEventArgs e) => Map.ShowGrid = GridToggle.IsChecked;
    private void OnCloseDetails(object sender, RoutedEventArgs e) { DetailLayout.IsPaneOpen = false; Map.Focus(FocusState.Programmatic); }
    private void OnCoordinateOpening(object sender, object e)
    {
        var camera = Map.Camera; CoordinateX.Value = Math.Floor(camera.X); CoordinateY.Value = Math.Floor(camera.Y);
        CoordinateError.Text = "";
        CoordinateRange.Text = _currentLayer?.Layer.CoordinateBounds is { } b
            ? I18n.Format("Preview_CoordinateRange", ViewModel.HorizontalAxis, b.MinX, b.MaxX, ViewModel.VerticalAxis, b.MinY, b.MaxY) : "";
    }
    private void OnLocate(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CoordinateX.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var x)
            && double.TryParse(CoordinateY.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var y))
        {
            if (Map.Locate(x, y)) { CoordinateError.Text = ""; CoordinateFlyout.Hide(); }
            else CoordinateError.Text = I18n.GetString("Preview_InvalidCoordinate");
        }
        else CoordinateError.Text = I18n.GetString("Preview_InvalidCoordinate");
    }
    private void OnQuickNavigate(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (ViewModel.Navigation?.QuickTargetId is { } id && ViewModel.Targets.FirstOrDefault(t => t.Target.Id == id) is { } target) Navigate(target.Target);
        else NavigationFlyout.ShowAt(NavigateButton);
    }
    private void OnTargetClicked(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is PreviewTargetChoice target) Navigate(target.Target); }
    private void Navigate(SpatialPreviewTarget target, bool preserveMode = true)
    {
        var layer = ViewModel.Layers.FirstOrDefault(l => l.Layer.Id == target.LayerId);
        if (layer is null) { ViewModel.NavigationStatus = I18n.GetString("Preview_TargetUnavailable"); return; }
        var oldMode = preserveMode ? ViewModel.CurrentMode?.Mode.Id : null;
        Layers.SelectedItem = layer;
        if (oldMode is not null && ViewModel.Modes.FirstOrDefault(m => m.Mode.Id == oldMode) is { } mode) ModeSelector.SelectedItem = mode;
        if (target.SuggestedHeight is { } height && ViewModel.CurrentMode?.Mode.UseNavigationHeight == true
            && (preserveMode || ViewModel.CurrentMode.Mode.UseNavigationHeightAsDefault))
        { ViewModel.ApplySuggestedHeight(height); CommitHeight(); }
        if (Map.Locate(target.X, target.Y)) NavigationFlyout.Hide();
        else ViewModel.NavigationStatus = I18n.GetString("Preview_InvalidCoordinate");
    }
    private void OnCancelPreparation(object sender, RoutedEventArgs e) => ViewModel.CancelPreparation();
    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        if (_navigation is not { } navigation) return;
        var folder = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == navigation.ConfigId)?.SourceFolders
            .FirstOrDefault(f => Guid.TryParse(f.Id, out var id) && id == navigation.FolderId);
        NavigationService.NavigateTo("History", folder is null ? ManagerNavigationParameter.ForConfig(navigation.ConfigId)
            : ManagerNavigationParameter.ForFolder(navigation.ConfigId, folder.Path));
    }
    private void OnRefresh(object sender, RoutedEventArgs e) => TaskObserver.Observe(LoadAsync(), nameof(SpatialPreviewPage));
}
