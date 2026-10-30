using FolderRewind.Controls;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using FolderRewind.Services.Plugins.V3;
using System.Collections.Generic;

namespace FolderRewind.Views;

public sealed partial class SpatialWorkspacePage : Page
{
    public SpatialWorkspaceViewModel ViewModel { get; } = new();
    private bool _binding, _syncing, _active, _rightOnly, _overlayLoading, _overlayPending, _zoomNext;
    private string? _cameraKey;
    private bool _cameraInitialized;
    private long _overlayGeneration;
    private (string? World, string? Layer, double X, double Y, double Scale)? _beforeLocation;
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private SpatialPreviewCanvas VisibleMap => _rightOnly && !ViewModel.IsScope ? RightMap : LeftMap;
    public SpatialWorkspacePage()
    {
        InitializeComponent();
        LeftMap.UseRoundedClip = RightMap.UseRoundedClip = true;
        LeftMap.MaximumCachedTiles = RightMap.MaximumCachedTiles = 128;
        LeftMap.MaximumConcurrentRequests = RightMap.MaximumConcurrentRequests = 2;
        AutomationProperties.SetAutomationId(LeftMap, "SpatialWorkspace_LeftMap");
        AutomationProperties.SetAutomationId(RightMap, "SpatialWorkspace_RightMap");
        ViewModel.MapsChanged += UpdateMaps; ViewModel.ChoicesChanged += UpdateChoices;
        ViewModel.OverlayChanged += QueueOverlays; ViewModel.LocateRequested += Locate;
        LeftMap.SelectionRequested += Select; RightMap.SelectionRequested += Select;
        LeftMap.CameraChanged += (x, y, scale) => Camera(LeftMap, RightMap, x, y, scale);
        RightMap.CameraChanged += (x, y, scale) => Camera(RightMap, LeftMap, x, y, scale);
        LeftMap.DetailsChanged += MapDetails; RightMap.DetailsChanged += MapDetails;
        LeftMap.OverlayInvoked += Overlay; RightMap.OverlayInvoked += Overlay;
        LeftMap.CursorChanged += Position; RightMap.CursorChanged += Position;
        LeftMap.ScaleChanged += UpdateScale; RightMap.ScaleChanged += UpdateScale;
        LeftMap.ActivityChanged += (loading, refining) => ViewModel.SetMapActivity(true, loading, refining);
        RightMap.ActivityChanged += (loading, refining) => ViewModel.SetMapActivity(false, loading, refining);
        LeftMap.StatusChanged += text => ViewModel.SetMapStatus(true, text);
        RightMap.StatusChanged += text => ViewModel.SetMapStatus(false, text);
        _overlayTimer.Tick += async (_, _) => { _overlayTimer.Stop(); await UpdateOverlaysAsync(); };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await GuardAsync(() => ViewModel.ReloadRowsAsync(true)); };
        SizeChanged += (_, _) => { Resize(); InitializeCamera(); };
        SidePanel.SizeChanged += (_, _) => DetailsScroll.MaxHeight = Math.Max(40, SidePanel.ActualHeight * .4 - 48);
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        if (e.Parameter is SpatialWorkspaceNavigationParameter navigation) await ViewModel.InitializeAsync(navigation);
        if (_active) { UpdateChoices(); UpdateMaps(); Resize(); }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false; _overlayGeneration++; _overlayTimer.Stop(); _searchTimer.Stop();
        ViewModel.MapsChanged -= UpdateMaps; ViewModel.ChoicesChanged -= UpdateChoices; ViewModel.OverlayChanged -= QueueOverlays; ViewModel.LocateRequested -= Locate;
        LeftMap.NavigationEnabled = RightMap.NavigationEnabled = false;
        LeftMap.Clear(); RightMap.Clear(); TaskObserver.Observe(ViewModel.DisposeAsync().AsTask(), "Spatial workspace disposal"); base.OnNavigatedFrom(e);
    }
    private void UpdateChoices()
    {
        _binding = true;
        try
        {
            LeftInput.SelectedItem = ViewModel.Left; RightInput.SelectedItem = ViewModel.Right;
            Worlds.SelectedItem = CompactWorlds.SelectedItem = ViewModel.World; Layers.SelectedItem = ViewModel.Layer; Modes.SelectedItem = ViewModel.Mode;
            if (ViewModel.HeightRange is { } height) { HeightInput.Minimum = height.Minimum; HeightInput.Maximum = height.Maximum; HeightInput.Header = PluginV3SpatialPreview.Localize(height.Label); }
            HeightInput.Value = ViewModel.Height;
            CategoryFilter.SelectedItem = ViewModel.Categories.FirstOrDefault(c => c.Id == ViewModel.Category);
            var states = ViewModel.IsMerge ? new[] { "", "Unresolved", "Automatic", "Resolved", "Locked", "Blocked" }
                : new[] { "", "Added", "Deleted", "Modified", "ContainerOnly", "Unavailable" };
            Filter.ItemsSource = states.Select(s => new SpatialWorkspaceChoice(s, I18n.GetString(s == "" ? "SpatialWorkspace_AllCategories" : "SpatialWorkspace_State_" + s))).ToArray();
            Filter.ItemTemplate = (DataTemplate)Resources["ChoiceTemplate"];
            if (!states.Contains(ViewModel.Filter)) ViewModel.Filter = "";
            Filter.SelectedItem = ((SpatialWorkspaceChoice[])Filter.ItemsSource).First(c => c.Id == ViewModel.Filter);
            CoordinateButton.Visibility = ViewModel.IsCompare ? Visibility.Collapsed : Visibility.Visible;
            Filter.Visibility = ViewModel.IsScope ? Visibility.Collapsed : Visibility.Visible;
            SwapInputs.Visibility = ViewModel.IsScope ? Visibility.Collapsed : Visibility.Visible;
            var d = ViewModel.LeftDescription ?? ViewModel.RightDescription;
            var x = d is null ? "X" : PluginV3SpatialPreview.Localize(d.HorizontalAxis);
            var y = d is null ? "Y" : PluginV3SpatialPreview.Localize(d.VerticalAxis);
            SelectionX1.Header = x + "₁"; SelectionX2.Header = x + "₂"; SelectionY1.Header = y + "₁"; SelectionY2.Header = y + "₂";
        }
        finally { _binding = false; }
    }
    private void UpdateMaps()
    {
        if (!_active) return;
        _overlayGeneration++;
        if (_cameraKey != ViewModel.CameraKey && (ViewModel.LeftSession is not null || ViewModel.RightSession is not null))
        {
            if (_cameraKey is not null && _cameraInitialized) { var camera = VisibleMap.Camera; ViewModel.RememberCamera(_cameraKey, camera.X, camera.Y, camera.Scale); }
            _cameraKey = ViewModel.CameraKey; _cameraInitialized = false;
        }
        _syncing = true;
        try { Set(LeftMap, ViewModel.LeftSession, ViewModel.LeftDescription, LeftEmpty); Set(RightMap, ViewModel.RightSession, ViewModel.RightDescription, RightEmpty); }
        finally { _syncing = false; }
        LeftMap.IsLeftComparisonSide = true; RightMap.IsLeftComparisonSide = false;
        LeftMap.CompareOverlays = RightMap.CompareOverlays = ViewModel.IsCompare;
        InitializeCamera(); QueueOverlays();
        void Set(SpatialPreviewCanvas map, PluginV3SpatialPreview? session, SpatialPreviewDescription? description, TextBlock empty)
        {
            var layer = description?.Layers.FirstOrDefault(l => l.Id == ViewModel.Layer?.Id);
            var mode = layer?.ViewModes.FirstOrDefault(m => m.Id == ViewModel.Mode?.Id);
            var available = session is not null && layer is not null && (ViewModel.Mode is null || mode is not null);
            empty.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            map.NavigationEnabled = ViewModel.LeftSession is not null || ViewModel.RightSession is not null;
            map.SelectionTool = !ViewModel.IsCompare && (SelectionTool.IsChecked == true || ScopeAdd.IsChecked == true || ScopeRemove.IsChecked == true);
            map.SetOverlays([]); map.SetSource(available ? session : null, available ? layer : null, (int)ViewModel.Height, false, ViewModel.Mode?.Id);
        }
    }
    private void InitializeCamera()
    {
        if (_cameraInitialized || ViewModel.CurrentLayer() is null || VisibleMap.ActualWidth < 1 || VisibleMap.ActualHeight < 1) return;
        _cameraInitialized = true;
        if (ViewModel.InitialCamera is { } camera) VisibleMap.RestoreCamera(camera.X, camera.Y, camera.Scale); else Fit();
    }
    private void Fit()
    {
        var layers = (ViewModel.LeftDescription?.Layers ?? []).Concat(ViewModel.RightDescription?.Layers ?? []).Where(l => l.Id == ViewModel.Layer?.Id).ToArray();
        if (layers.Length == 0) return;
        VisibleMap.FitBounds(new(layers.Min(l => l.Bounds.MinX), layers.Min(l => l.Bounds.MinY), layers.Max(l => l.Bounds.MaxX), layers.Max(l => l.Bounds.MaxY)));
    }
    private void Camera(SpatialPreviewCanvas source, SpatialPreviewCanvas target, double x, double y, double scale)
    {
        if (!_active || _syncing) return;
        _syncing = true; try { target.RestoreCamera(x, y, scale); } finally { _syncing = false; }
        if (_cameraInitialized && _cameraKey is not null) ViewModel.RememberCamera(_cameraKey, x, y, scale);
        ViewModel.RememberViewport(source.VisibleBounds); QueueOverlays();
    }
    private void Position(double x, double y)
    {
        var description = ViewModel.LeftDescription ?? ViewModel.RightDescription;
        if (description is not null) Coordinates.Text = PluginV3SpatialPreview.Localize(description.HorizontalAxis) + " " + Math.Floor(x) + " · " + PluginV3SpatialPreview.Localize(description.VerticalAxis) + " " + Math.Floor(y);
    }
    private void UpdateScale(double units, double pixels)
    { ScaleLine.Width = Math.Min(120, pixels); ScaleText.Text = units + " " + ((ViewModel.LeftDescription?.DistanceUnit ?? ViewModel.RightDescription?.DistanceUnit) is { } unit ? PluginV3SpatialPreview.Localize(unit) : ""); }
    private void QueueOverlays() { if (_active && !_overlayTimer.IsEnabled) _overlayTimer.Start(); }
    private async Task UpdateOverlaysAsync()
    {
        if (_overlayLoading) { _overlayPending = true; return; }
        _overlayLoading = true; var generation = ++_overlayGeneration;
        try
        {
            var overlays = await ViewModel.OverlaysAsync(VisibleMap.VisibleBounds, VisibleMap.Camera.Scale);
            if (_active && generation == _overlayGeneration) { LeftMap.SetOverlays(overlays); RightMap.SetOverlays(overlays); }
        }
        catch (Exception ex) { if (_active) Details(ex.Message); }
        finally { _overlayLoading = false; if (_overlayPending) { _overlayPending = false; QueueOverlays(); } }
    }
    private async void Select(SpatialPreviewBounds bounds, bool additive, bool remove) => await GuardAsync(() => ViewModel.SelectAreaAsync(bounds, ViewModel.IsScope || additive, ScopeRemove.IsChecked == true || remove));
    private async Task GuardAsync(Func<Task> action)
    { try { await action(); } catch (Exception ex) { if (_active) Details(I18n.GetString("SpatialWorkspace_Failed") + "\n" + ex.Message); LogService.LogWarning(ex.ToString(), "SpatialWorkspace"); } }
    private Task SelectCoordinatesAsync(bool remove)
    {
        var values = new[] { SelectionX1.Value, SelectionY1.Value, SelectionX2.Value, SelectionY2.Value };
        if (values.Any(v => !double.IsFinite(v)) || values[0] == values[2] || values[1] == values[3]) { Details(I18n.GetString("SpatialWorkspace_InvalidCoordinates")); return Task.CompletedTask; }
        return ViewModel.SelectAreaAsync(new(Math.Min(values[0], values[2]), Math.Min(values[1], values[3]), Math.Max(values[0], values[2]), Math.Max(values[1], values[3])), true, remove);
    }
    private async void OnSelectCoordinates(object sender, RoutedEventArgs e) => await GuardAsync(() => SelectCoordinatesAsync(false));
    private async void OnRemoveCoordinates(object sender, RoutedEventArgs e) => await GuardAsync(() => SelectCoordinatesAsync(true));
    private void MapDetails(string text) { ViewModel.ClearFocus(); Details(text); }
    private void Details(string text) { ViewModel.ShowDetails(text); Workspace.IsPaneOpen = true; DetailsExpander.IsExpanded = true; }
    private void RememberLocation()
    { var c = VisibleMap.Camera; _beforeLocation = (ViewModel.World?.Id, ViewModel.Layer?.Id, c.X, c.Y, c.Scale); BackCamera.IsEnabled = true; }
    private async void Locate(SpatialSubjectLocation location) => await GuardAsync(async () =>
    {
        RememberLocation();
        if (ViewModel.World?.Id != location.WorldId) { ViewModel.World = ViewModel.Worlds.FirstOrDefault(w => w.Id == location.WorldId); await ViewModel.ChangeWorldAsync(); }
        if (location.LayerId is { } layerId && ViewModel.Layer?.Id != layerId) { ViewModel.Layer = ViewModel.Layers.FirstOrDefault(l => l.Id == layerId); ViewModel.ChangeLayer(); }
        if (location.Bounds is { } b)
        {
            if (_zoomNext) VisibleMap.FitBounds(b);
            else VisibleMap.Locate((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, true);
        }
        else Fit();
        _zoomNext = false; UpdateChoices(); QueueOverlays();
    });
    private async void Overlay(SpatialOverlayRegion overlay, bool twice) => await GuardAsync(async () =>
    {
        if (overlay.Aggregate)
        {
            ViewModel.ClearFocus();
            Details(I18n.Format("SpatialWorkspace_Aggregate", overlay.Count) + "\n" + string.Join(" · ", overlay.States.Select(s => I18n.GetString("SpatialWorkspace_State_" + s))) + "\n" + string.Join(" · ", overlay.Components));
            if (twice) { RememberLocation(); VisibleMap.FitBounds(overlay.Bounds); }
        }
        else { await ViewModel.SelectOverlayAsync(overlay.Ids); DetailsExpander.IsExpanded = true; if (twice) ViewModel.LocateSelected(); }
    });
    private void Resize()
    {
        if (Workspace is null) return;
        var narrow = ActualWidth < 760;
        var display = ActualWidth < 1200 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (Workspace.DisplayMode != display) { Workspace.DisplayMode = display; Workspace.IsPaneOpen = display == SplitViewDisplayMode.Inline; }
        var leftVisible = ViewModel.IsScope || !narrow || !_rightOnly; var rightVisible = !ViewModel.IsScope && (!narrow || _rightOnly);
        LeftPane.Visibility = leftVisible ? Visibility.Visible : Visibility.Collapsed; RightPane.Visibility = rightVisible ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = leftVisible ? new(1, GridUnitType.Star) : new(0); RightColumn.Width = rightVisible ? new(1, GridUnitType.Star) : new(0);
        SingleSide.Visibility = narrow && !ViewModel.IsScope ? Visibility.Visible : Visibility.Collapsed;
        Worlds.Visibility = ActualWidth < 900 || ViewModel.Worlds.Count < 2 ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void OnInputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || !_active || ViewModel.IsBusy || sender is ComboBox { SelectedItem: null }) return;
        ViewModel.Left = LeftInput.SelectedItem as SpatialWorkspaceChoice; ViewModel.Right = RightInput.SelectedItem as SpatialWorkspaceChoice;
        await ViewModel.ChangeInputsAsync(); UpdateChoices(); UpdateMaps();
    }
    private async void OnWorldChanged(object sender, SelectionChangedEventArgs e)
    { if (_binding || !_active || ViewModel.IsBusy || sender is ComboBox { SelectedItem: null }) return; ViewModel.ClearFocus(); ViewModel.World = (sender as ComboBox)?.SelectedItem as SpatialWorkspaceChoice; await ViewModel.ChangeWorldAsync(); UpdateChoices(); }
    private void OnLayerChanged(object sender, SelectionChangedEventArgs e)
    { if (_binding || !_active || ViewModel.IsBusy || sender is ComboBox { SelectedItem: null }) return; ViewModel.ClearFocus(); ViewModel.Layer = Layers.SelectedItem as SpatialWorkspaceChoice; ViewModel.ChangeLayer(); }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    { if (_binding || !_active || Modes.SelectedItem is null) return; ViewModel.Mode = Modes.SelectedItem as SpatialWorkspaceChoice; ViewModel.ChangeMode(); }
    private void OnHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    { if (_binding || !_active || !double.IsFinite(args.NewValue)) return; ViewModel.Height = args.NewValue; UpdateMaps(); }
    private void OnSearch(object sender, TextChangedEventArgs e) { if (_active) { _searchTimer.Stop(); _searchTimer.Start(); } }
    private async void OnFilter(object sender, SelectionChangedEventArgs e)
    { if (!_active || _binding || Filter.SelectedItem is null) return; ViewModel.Filter = (Filter.SelectedItem as SpatialWorkspaceChoice)?.Id ?? ""; await GuardAsync(() => ViewModel.ReloadRowsAsync(true)); }
    private async void OnCategory(object sender, SelectionChangedEventArgs e)
    { if (!_active || _binding || CategoryFilter.SelectedItem is null) return; ViewModel.Category = (CategoryFilter.SelectedItem as SpatialWorkspaceChoice)?.Id ?? "$all"; await GuardAsync(() => ViewModel.ReloadRowsAsync(true)); }
    private async void OnItemClick(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is SpatialWorkspaceItem item) { await ViewModel.SelectItemAsync(item, false); DetailsExpander.IsExpanded = true; } }
    private void OnItemContainer(ListViewBase sender, ContainerContentChangingEventArgs args)
    { if (args.Item is SpatialWorkspaceItem item) AutomationProperties.SetName(args.ItemContainer, item.AutomationName); }
    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { ViewModel.LocateSelected(); e.Handled = true; }
    private async void OnCheckItem(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: SpatialWorkspaceItem item } box) return;
        if (!ViewModel.CanDecide) { box.IsChecked = item.IsChecked; return; }
        await GuardAsync(() => ViewModel.CheckItemAsync(item, box.IsChecked == true));
    }
    private void OnLocateSelected(object sender, RoutedEventArgs e) => ViewModel.LocateSelected();
    private void OnZoomSelected(object sender, RoutedEventArgs e) { _zoomNext = true; ViewModel.LocateSelected(); }
    private void OnBack(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }
    private void OnToggleList(object sender, RoutedEventArgs e) => Workspace.IsPaneOpen = !Workspace.IsPaneOpen;
    private async void OnBatchScope(object sender, RoutedEventArgs e)
    { if (sender is CheckBox box) await GuardAsync(() => ViewModel.ChangeBatchModeAsync(box.IsChecked != false)); }
    private void OnSelectionTool(object sender, RoutedEventArgs e) { LeftMap.SelectionTool = RightMap.SelectionTool = SelectionTool.IsChecked == true; }
    private void OnScopeTool(object sender, RoutedEventArgs e)
    { var add = ReferenceEquals(sender, ScopeAdd); ScopeAdd.IsChecked = add; ScopeRemove.IsChecked = !add; LeftMap.SelectionTool = true; }
    private void OnPan(object sender, RoutedEventArgs e) { ScopeAdd.IsChecked = ScopeRemove.IsChecked = SelectionTool.IsChecked = false; LeftMap.SelectionTool = RightMap.SelectionTool = false; }
    private void OnFit(object sender, RoutedEventArgs e) => Fit();
    private void OnZoomIn(object sender, RoutedEventArgs e) => VisibleMap.Zoom(2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => VisibleMap.Zoom(.5);
    private void OnSwitchSide(object sender, RoutedEventArgs e) { _rightOnly = !_rightOnly; SingleSide.Content = I18n.GetString(_rightOnly ? "SpatialWorkspace_ShowA" : "SpatialWorkspace_ShowB"); Resize(); }
    private async void OnSwap(object sender, RoutedEventArgs e)
    { if (ViewModel.IsBusy) return; (ViewModel.Left, ViewModel.Right) = (ViewModel.Right, ViewModel.Left); await ViewModel.ChangeInputsAsync(); UpdateChoices(); }
    private async void OnBackCamera(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_beforeLocation is not { } previous) return;
        if (previous.World != ViewModel.World?.Id) { ViewModel.World = ViewModel.Worlds.FirstOrDefault(w => w.Id == previous.World); await ViewModel.ChangeWorldAsync(); }
        if (previous.Layer != ViewModel.Layer?.Id) { ViewModel.Layer = ViewModel.Layers.FirstOrDefault(l => l.Id == previous.Layer); ViewModel.ChangeLayer(); }
        VisibleMap.RestoreCamera(previous.X, previous.Y, previous.Scale); _beforeLocation = null; BackCamera.IsEnabled = false;
    });
    private async void OnCancel(object sender, RoutedEventArgs e) => await ViewModel.CancelComparisonAsync();
    private async void OnPreviousPage(object sender, RoutedEventArgs e) => await GuardAsync(() => ViewModel.PageAsync(-1));
    private async void OnNextPage(object sender, RoutedEventArgs e) => await GuardAsync(() => ViewModel.PageAsync(1));
    private async Task<bool> ConfirmAsync(string key, string? summary = null)
    {
        var text = I18n.GetString(key) + "\n" + (summary ?? ViewModel.Counts);
        if (key == "SpatialWorkspace_ConfirmScope") text += "\n\n" + ViewModel.Details;
        var content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = I18n.GetString("SpatialWorkspace_ConfirmTitle"), Content = content,
            PrimaryButtonText = I18n.GetString("SpatialWorkspace_Confirm"), CloseButtonText = I18n.GetString("SpatialWorkspace_CancelText"), DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async void OnDecide(object sender, RoutedEventArgs e)
    {
        if (!Enum.TryParse<MergeResolutionChoice>((sender as FrameworkElement)?.Tag as string, out var choice)) return;
        await GuardAsync(async () =>
        {
            var selection = await ViewModel.CaptureDecisionAsync(); if (selection is null) return;
            var summary = I18n.Format("SpatialWorkspace_SelectedCount", selection.Ids.Length);
            if (selection.PreviouslyDecided > 0 && !await ConfirmAsync("SpatialWorkspace_ConfirmOverwrite", I18n.Format("SpatialWorkspace_OverwriteCount", selection.PreviouslyDecided))) return;
            if (choice == MergeResolutionChoice.Delete && !await ConfirmAsync("SpatialWorkspace_ConfirmDelete", summary)) return;
            if (selection.WholeDomain && !await ConfirmAsync("SpatialWorkspace_ConfirmWorld", summary)) return;
            await ViewModel.DecideAsync(choice, selection);
        });
    }
    private async void OnResetDecision(object sender, RoutedEventArgs e) => await ViewModel.DecideAsync(null);
    private async void OnUndo(object sender, RoutedEventArgs e) => await ViewModel.UndoAsync();
    private async void OnNextConflict(object sender, RoutedEventArgs e) => await GuardAsync(() => ViewModel.NextUnresolvedAsync());
    private async void OnGenerate(object sender, RoutedEventArgs e) => await ViewModel.GenerateResultAsync();
    private async void OnPreviewScope(object sender, RoutedEventArgs e) => await ViewModel.PreviewScopeAsync();
    private async void OnSaveScope(object sender, RoutedEventArgs e) { await ViewModel.PreviewScopeAsync(); if (string.IsNullOrEmpty(ViewModel.Error) && await ConfirmAsync("SpatialWorkspace_ConfirmScope")) await ViewModel.SaveScopeAsync(); }
    private async void OnInheritScope(object sender, RoutedEventArgs e) { if (await ConfirmAsync("SpatialWorkspace_ConfirmScope")) await ViewModel.ResetScopeAsync(true); }
    private async void OnFullScope(object sender, RoutedEventArgs e) { if (await ConfirmAsync("SpatialWorkspace_ConfirmScope")) await ViewModel.ResetScopeAsync(false); }
}
