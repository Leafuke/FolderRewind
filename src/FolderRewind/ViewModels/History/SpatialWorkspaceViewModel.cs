using FolderRewind.Controls;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record SpatialWorkspaceChoice(string Id, string Name);
public sealed class SpatialWorkspaceItem : ViewModelBase
{
    internal SpatialWorkspaceRow Row { get; }
    internal SpatialWorkspaceItem(SpatialWorkspaceRow row) => Row = row;
    public string Id => Row.Id;
    public string Title => Row.Title;
    public string Description => Row.Description;
    public string Status => I18n.GetString("SpatialWorkspace_State_" + Row.Status);
    public bool IsChecked => Row.Checked;
    public bool CanCheck => Row.CanSelect;
    public string Categories { get; init; } = "";
    public string Secondary => string.Join(" · ", new[] { Status, Categories }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string SelectionAutomationId => "SpatialWorkspace_Check_" + Id;
    public string AutomationName => Title + " · " + Status + " · " + Categories;
}

public sealed partial class SpatialWorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    private SpatialWorkspaceNavigationParameter _navigation = null!;
    private BackupConfig _config = null!;
    private ConfigSnapshot _origin = null!;
    private readonly Dictionary<string, SpatialWorkspaceInput> _inputs = new(StringComparer.Ordinal);
    private readonly List<HistoricalPreviewWorkspace> _ownedInputs = [];
    private readonly Dictionary<string, HistoricalPreviewWorkspace> _historyByKey = new(StringComparer.Ordinal);
    private string? _viewStatePath;
    private SpatialWorkspaceViewState _viewState = new();
    private SpatialPreviewBounds? _visibleBounds;
    internal void RememberViewport(SpatialPreviewBounds bounds) => _visibleBounds = bounds;
    private OwnedPluginTemporaryStorage? _storage;
    private SpatialWorkspaceIndex? _index;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scanCancellation;
    private Task _scan = Task.CompletedTask;
    private bool _active = true;
    private TaskCompletionSource? _operationCompletion;
    private CancellationTokenSource? _operationCancellation;
    private int _offset;
    private bool _leftRendering, _rightRendering;
    private string _leftMapStatus = "", _rightMapStatus = "";
    private Guid _indexedPlan;
    private bool _mergeProviderAvailable;
    internal MergeOperationService? Operations { get; private set; }
    private ConfigSettingsDraftService? _scopeDraft;
    internal PluginV3SpatialPreview? LeftSession { get; private set; }
    internal PluginV3SpatialPreview? RightSession { get; private set; }
    internal SpatialPreviewDescription? LeftDescription { get; private set; }
    internal SpatialPreviewDescription? RightDescription { get; private set; }
    public ObservableCollection<SpatialWorkspaceChoice> Inputs { get; } = [];
    public ObservableCollection<SpatialWorkspaceChoice> Worlds { get; } = [];
    public ObservableCollection<SpatialWorkspaceChoice> Layers { get; } = [];
    public ObservableCollection<SpatialWorkspaceChoice> Modes { get; } = [];
    public ObservableCollection<SpatialWorkspaceItem> Items { get; } = [];
    public SpatialWorkspaceChoice? Left { get; set; }
    public SpatialWorkspaceChoice? Right { get; set; }
    public SpatialWorkspaceChoice? World { get; set; }
    public SpatialWorkspaceChoice? Layer { get; set; }
    public SpatialWorkspaceChoice? Mode { get; set; }
    public double Height { get; set; } = 64;
    public string Search { get; set; } = "";
    public string Filter { get; set; } = "";
    public SpatialWorkspaceItem? SelectedItem { get; set; }
    public string Title { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string Error { get; private set; } = "";
    public string Counts { get; private set; } = "";
    public string Details { get; set; } = "";
    public string PolicyDescription { get; private set; } = "";
    public bool IsBusy { get; private set; }
    public bool IsScanning { get; private set; }
    public bool HasActivity => IsBusy || IsScanning || _leftRendering || _rightRendering;
    public string MapStatus => string.Join(" · ", new[] { _leftMapStatus, _rightMapStatus }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    internal void SetMapActivity(bool left, bool loading, bool refining)
    { if (left) _leftRendering = loading || refining; else _rightRendering = loading || refining; OnPropertyChanged(nameof(HasActivity)); }
    internal void SetMapStatus(bool left, string text)
    { if (left) _leftMapStatus = text; else _rightMapStatus = text; OnPropertyChanged(nameof(MapStatus)); }
    public bool CanCancel => IsBusy || IsScanning;
    public bool NeedsDownload { get; private set; }
    internal string CameraKey => System.Text.Json.JsonSerializer.Serialize(new[] { World?.Id, Layer?.Id });
    internal (double X, double Y, double Scale)? InitialCamera => _viewState.Cameras.TryGetValue(CameraKey, out var camera) ? (camera.X, camera.Y, camera.Scale) : null;
    internal void RememberCamera(string key, double x, double y, double scale)
    { _viewState.Cameras[key] = new(x, y, scale); }
    public bool IsScope => _navigation?.Mode == SpatialWorkspaceMode.Scope;
    public bool IsMerge => _navigation?.Mode == SpatialWorkspaceMode.Merge;
    public bool CanEdit => !IsBusy && _active && (!IsMerge || Operations?.Snapshot.Session?.Plan.Revision == _indexedPlan);
    public bool CanDecide => CanEdit && _mergeProviderAvailable && Operations?.Snapshot is { IsBusy: false, IsSaved: true, Session.State: MergeSessionState.Ready or MergeSessionState.Resolving }
        && Operations.Snapshot.Session!.Plan.Revision == _indexedPlan;
    public bool CanGenerate => CanDecide && Operations?.Snapshot.Session?.State == MergeSessionState.Ready;
    public bool CanUndo => CanDecide && Operations?.CanUndo == true;
    internal SpatialPreviewHeight? HeightRange => CurrentLayer()?.ViewModes.FirstOrDefault(m => m.Id == Mode?.Id)?.Height ?? CurrentLayer()?.Height;
    public bool CanChangeHeight => CanEdit && HeightRange is not null;
    public bool HasMaps => LeftSession is not null;
    public bool CanNextPage { get; private set; }
    public event Action? MapsChanged;
    public event Action? OverlayChanged;
    public event Action? ChoicesChanged;
    public event Action<SpatialSubjectLocation>? LocateRequested;
    private void Notify()
    {
        foreach (var name in new[] { nameof(Title), nameof(Status), nameof(Error), nameof(Counts), nameof(Details), nameof(IsBusy), nameof(IsScanning), nameof(HasActivity), nameof(CanCancel), nameof(NeedsDownload), nameof(CanEdit), nameof(CanDecide), nameof(CanGenerate), nameof(CanUndo), nameof(CanChangeHeight), nameof(IsScope), nameof(IsMerge), nameof(HasMaps), nameof(CanNextPage), nameof(IsCompare), nameof(HasSpatialIndex), nameof(HasDetails), nameof(FocusLabel), nameof(CanLocate), nameof(CategorySummary), nameof(CategoryBreakdown), nameof(HasCategorySummary), nameof(BatchOnlyUnresolved), nameof(SelectionFeedback), nameof(HasSelectionFeedback), nameof(LeftLabel), nameof(RightLabel), nameof(ModeLegend) }) OnPropertyChanged(name);
    }
    public async Task InitializeAsync(SpatialWorkspaceNavigationParameter navigation)
    {
        _navigation = navigation;
        await RunAsync(async token =>
        {
            var original = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == navigation.ConfigId);
            _config = ConfigSnapshotMapper.Copy(original); _origin = PluginV3ModelMapper.ToSnapshot(_config);
            Title = I18n.GetString("SpatialWorkspace_Title_" + navigation.Mode) + " · " + _config.SourceFolders.Single(f => Guid.Parse(f.Id) == navigation.FolderId).DisplayName;
            var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(_config, token);
            _viewStatePath = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "spatial-view-state", $"{navigation.FolderId:N}-{navigation.Mode}-{navigation.SessionId:N}.json");
            _viewState = await Task.Run(() => SpatialWorkspaceViewStateStore.Load(_viewStatePath), token);
            Search = _viewState.Search; Filter = _viewState.PresentationVersion < 2 && IsMerge ? "Unresolved" : _viewState.Filter; Height = _viewState.Height;
            OnPropertyChanged(nameof(Search));
            _storage = await OwnedPluginTemporaryStorage.CreateAsync(NativeHistoryApplicationService.RequirePreviewRoot(_config, runtime.Repository.Paths.RepositoryRoot), token);
            var indexRoot = await _storage.CreateDirectoryAsync(token); _index = new(Path.Combine(indexRoot, "spatial.db"));
            if (IsScope)
            {
                _scopeDraft = new(original); Inputs.Add(new("live", I18n.GetString("SpatialWorkspace_Live")));
                _inputs.Add("live", new("live", null, false));
            }
            else if (IsMerge)
            {
                Operations = MergeOperationService.Get(original, new(navigation.FolderId));
                await Operations.LoadAsync(navigation.SessionId);
                if (Operations.Snapshot.Session is not { } session) throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_NoSession"));
                _indexedPlan = session.Plan.Revision;
                var policy = PluginV3SemanticMerge.Options(_config).FirstOrDefault(o => o.Binding == session.Plan.SemanticProvider);
                _mergeProviderAvailable = policy is not null;
                if (!_mergeProviderAvailable) Error = Status = I18n.GetString("SpatialWorkspace_OldMergePolicy");
                PolicyDescription = policy is null ? I18n.GetString("Merge_SemanticUnavailable") : policy.Name + " · " + policy.Description;
                OnPropertyChanged(nameof(PolicyDescription));
                Inputs.Add(new("ours", session.Plan.Ours.Name)); Inputs.Add(new("theirs", session.Plan.Theirs.Name)); Inputs.Add(new("base", I18n.GetString("SpatialWorkspace_Base")));
                await RefreshMergeIndexAsync(token);
                Operations.Tracker.Changed += OnOperationChanged;
            }
            else
            {
                var versions = await runtime.Query.GetAllVersionsAsync(token);
                foreach (var version in versions.Where(v => v.SourceId.Value == navigation.FolderId).OrderByDescending(v => v.CreatedAtUtc))
                    Inputs.Add(new(version.VersionId.ToString(), version.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + version.VersionId.ToString()[..8]));
            }
            Left = Inputs.FirstOrDefault(i => i.Id == navigation.VersionId?.ToString()) ?? Inputs.FirstOrDefault(i => i.Id == _viewState.Left) ?? Inputs.FirstOrDefault();
            Right = Inputs.FirstOrDefault(i => i.Id == _viewState.Right && i != Left) ?? Inputs.FirstOrDefault(i => i != Left) ?? Left;
            if (IsCompare && _viewState.Left is null && Inputs.IndexOf(Left!) < Inputs.IndexOf(Right!)) (Left, Right) = (Right, Left);
            Category = _viewState.PresentationVersion < 2 && IsCompare ? "$default" : _viewState.Category;
            if (_viewState.World is { } world) World = new(world, world);
            if (_viewState.Layer is { } layer) Layer = new(layer, layer);
            if (_viewState.Mode is { } mode) Mode = new(mode, mode);
            ChoicesChanged?.Invoke();
            await OpenPanesAsync(token);
            if (IsScope) LoadScopeSelection();
            if (IsMerge) SetCategories(new([]));
            if (!IsScope && !IsMerge) StartComparison();
            await ReloadRowsAsync();
        });
    }
    public Task ChangeInputsAsync() => RunAsync(async token =>
    {
        await StopComparisonAsync(); await ClosePanesAsync();
        if (!IsMerge)
        {
            foreach (var key in _historyByKey.Keys.Where(k => k != Left?.Id && k != Right?.Id).ToArray())
            { var old = _historyByKey[key]; _historyByKey.Remove(key); _ownedInputs.Remove(old); _inputs.Remove(key); await old.DisposeAsync(); }
        }
        if (!IsMerge) await Task.Run(() => _index!.Clear(), token);
        SelectedItem = null; Details = ""; await OpenPanesAsync(token);
        if (!IsScope && !IsMerge) StartComparison();
        await ReloadRowsAsync();
    });
    public Task ChangeWorldAsync() => RunAsync(async token => { await ClosePanesAsync(); await OpenPanesAsync(token); await ReloadRowsAsync(true); });
    public void ChangeLayer()
    {
        var layer = CurrentLayer(); var prior = Mode?.Id; Modes.Clear();
        foreach (var mode in layer?.ViewModes ?? []) Modes.Add(new(mode.Id, PluginV3SpatialPreview.Localize(mode.Name)));
        Mode = Modes.FirstOrDefault(m => m.Id == prior) ?? Modes.FirstOrDefault(m => m.Id == layer?.DefaultViewModeId) ?? Modes.FirstOrDefault();
        var height = layer?.ViewModes.FirstOrDefault(m => m.Id == Mode?.Id)?.Height ?? layer?.Height;
        Height = height is null ? 64 : prior is not null && Mode?.Id == prior ? Math.Clamp(Height, height.Minimum, height.Maximum) : height.DefaultValue;
        ChoicesChanged?.Invoke(); MapsChanged?.Invoke(); OverlayChanged?.Invoke(); Notify();
        TaskObserver.Observe(ReloadRowsAsync(true), "Spatial layer rows");
    }
    public void ChangeMode()
    {
        if (HeightRange is { } height) Height = height.DefaultValue;
        ChoicesChanged?.Invoke(); MapsChanged?.Invoke(); Notify();
    }
    internal SpatialPreviewLayer? CurrentLayer() => LeftDescription?.Layers.FirstOrDefault(l => l.Id == Layer?.Id) ?? RightDescription?.Layers.FirstOrDefault(l => l.Id == Layer?.Id);
    private async Task OpenPanesAsync(CancellationToken token)
    {
        if (Left is null) return;
        var left = await GetInputAsync(Left.Id, token); var right = IsScope || Right is null ? left : await GetInputAsync(Right.Id, token);
        LeftSession = Open(left); RightSession = IsScope ? null : Open(right);
        LeftDescription = await LeftSession.DescribeAsync(token);
        RightDescription = RightSession is null ? null : await RightSession.DescribeAsync(token);
        var worldId = World?.Id; Worlds.Clear();
        foreach (var w in (LeftDescription.Worlds.Concat(RightDescription?.Worlds ?? [])).DistinctBy(w => w.Id)) Worlds.Add(new(w.Id, PluginV3SpatialPreview.Localize(w.Name)));
        World = Worlds.FirstOrDefault(w => w.Id == worldId) ?? Worlds.FirstOrDefault();
        if (worldId != World?.Id && World is not null)
        {
            await ClosePanesAsync(); LeftSession = Open(left); RightSession = IsScope ? null : Open(right);
            LeftDescription = await LeftSession.DescribeAsync(token); RightDescription = RightSession is null ? null : await RightSession.DescribeAsync(token);
        }
        var layerId = Layer?.Id; Layers.Clear();
        foreach (var layer in LeftDescription.Layers.Concat(RightDescription?.Layers ?? []).DistinctBy(l => l.Id)) Layers.Add(new(layer.Id, PluginV3SpatialPreview.Localize(layer.Name)));
        Layer = Layers.FirstOrDefault(l => l.Id == layerId) ?? Layers.FirstOrDefault(); ChangeLayer();
        PluginV3SpatialPreview Open(SpatialWorkspaceInput input) => input.Fixed is { } fixedInput
            ? PluginV3SpatialPreview.OpenFixed(_origin, _navigation.FolderId, fixedInput, input.Partial, World?.Id)
            : PluginV3SpatialPreview.OpenWorld(_origin, _navigation.FolderId, World?.Id);
    }
    private async Task<SpatialWorkspaceInput> GetInputAsync(string key, CancellationToken token)
    {
        if (_inputs.TryGetValue(key, out var existing)) return existing;
        SpatialWorkspaceInput input;
        if (IsMerge)
            input = await Task.Run(() => SpatialWorkspaceInputPreparation.PrepareMergeAsync(Operations!, _navigation.FolderId, key, _storage!, token), token);
        else
        {
            var prepared = await Task.Run(() => SpatialWorkspaceInputPreparation.PrepareHistoryAsync(_config, _navigation.FolderId, key, token), token);
            input = prepared.Input; _ownedInputs.Add(prepared.Lease); _historyByKey.Add(key, prepared.Lease);
        }
        _inputs.Add(key, input); return input;
    }
    private async Task ClosePanesAsync()
    {
        var left = LeftSession; var right = RightSession; LeftSession = RightSession = null;
        MapsChanged?.Invoke();
        if (left is not null) await left.CloseAsync(); if (right is not null) await right.CloseAsync();
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || !_active) return;
        var completion = _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IsBusy = true; Error = ""; NeedsDownload = false; Status = I18n.GetString("SpatialWorkspace_Preparing"); Notify();
        try { await action(cancellation.Token); if (_active && Status == I18n.GetString("SpatialWorkspace_Preparing")) Status = I18n.GetString("SpatialWorkspace_Ready_" + _navigation.Mode); }
        catch (OperationCanceledException) { if (_active) Status = I18n.GetString("SpatialWorkspace_Canceled"); }
        catch (Exception ex) { if (_active) { NeedsDownload = ex is HistoricalPreviewUnavailableException { RequiresDownload: true }; Error = ex.Message; Status = I18n.GetString("SpatialWorkspace_Failed"); } LogService.LogWarning(ex.ToString(), "SpatialWorkspace"); }
        finally { _operationCancellation = null; IsBusy = false; if (_active) Notify(); completion.TrySetResult(); }
    }
    private void OnOperationChanged() => EnqueueOnUiThread(() =>
    {
        if (!_active) return;
        if (Operations!.Snapshot.Session?.Plan.Revision != _indexedPlan)
        { Error = I18n.GetString("SpatialWorkspace_PlanExpired"); TaskObserver.Observe(ClosePanesAsync(), "Spatial plan invalidation"); Notify(); return; }
        if (Operations!.Review is null && _inputs.Remove("result") && (Left?.Id == "result" || Right?.Id == "result"))
        { Error = I18n.GetString("SpatialWorkspace_ResultExpired"); TaskObserver.Observe(ClosePanesAsync(), "Spatial result invalidation"); }
        Notify();
    });
    public async ValueTask DisposeAsync()
    {
        _active = false; _lifetime.Cancel();
        if (_operationCompletion is { } pending) await pending.Task;
        if (_viewStatePath is { } statePath)
        {
            var state = _viewState with { Left = Left?.Id, Right = Right?.Id, World = World?.Id, Layer = Layer?.Id, Mode = Mode?.Id, Height = Height, Search = Search, Filter = Filter, Category = Category, PresentationVersion = 2 };
            try { await Task.Run(() => SpatialWorkspaceViewStateStore.Save(statePath, state)); } catch (Exception ex) { LogService.LogWarning(ex.ToString(), "Spatial view state"); }
        }
        if (Operations is not null) Operations.Tracker.Changed -= OnOperationChanged;
        await StopComparisonAsync(); await ClosePanesAsync(); _index?.Dispose();
        foreach (var input in _ownedInputs) await input.DisposeAsync();
        if (_storage is not null) await _storage.DeleteAsync(); _lifetime.Dispose();
    }
}
