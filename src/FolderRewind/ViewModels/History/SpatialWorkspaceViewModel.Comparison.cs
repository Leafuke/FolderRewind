using FolderRewind.Controls;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

internal sealed record SpatialDecisionSelection(Guid SessionId, Guid PlanRevision, long Revision, string[] Ids, bool WholeDomain, int PreviouslyDecided);

public sealed partial class SpatialWorkspaceViewModel
{
    private sealed record RowRequest(SpatialWorkspaceIndex Index, SpatialWorkspaceQuery Query, int Offset,
        bool Merge, bool DefaultCategory, long Revision);
    private sealed record ScanProgress(CancellationTokenSource Scan, long Files, long Changes);
    private SpatialPresentationRefreshCoordinator<RowRequest>? _rowRefresh;
    private ScanProgress? _scanProgress;
    private int _scanRefreshQueued;
    private bool _scanHasRequestedRows;
    private bool _scanCompleted;
    private long _rowsPublishedRevision;
    private long _lastOverlayReadVersion;
    internal long LastOverlayReadVersion => _lastOverlayReadVersion;
    internal SpatialIndexDiagnostics? SpatialDiagnostics => _index?.Diagnostics;

    private void StartComparison()
    {
        if (LeftSession is null || RightSession is null) return;
        var index = _index!;
        var stateNames = Enum.GetValues<SpatialChangeKind>().ToDictionary(k => k, k => I18n.GetString("SpatialWorkspace_State_" + k));
        var initial = new SpatialComparisonRequest(Guid.NewGuid(), LeftSession.Source, RightSession.Source, null, 500)
        { InitialPriority = World is null ? null : new SpatialSubjectLocation(World.Id, Layer?.Id, _visibleBounds) };
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _scanCancellation; var token = cancellation.Token; IsScanning = true; _scanHasRequestedRows = false; _scanCompleted = false;
        Volatile.Write(ref _scanProgress, new(cancellation, 0, 0));
        _scan = Task.Run(async () =>
        {
            try
            {
                var description = await PluginV3SpatialComparison.DescribeAsync(initial, token);
                await UiDispatcherService.RunOnUiAsync(() => { if (_active && !token.IsCancellationRequested) SetCategories(description); });
                long total = 0;
                await foreach (var page in PluginV3SpatialComparison.ReadAsync(initial, token))
                {
                    total += page.Changes.Count;
                    index.Put(page.Changes.Select(c => new SpatialWorkspaceRow(c.Id, PluginV3SpatialPreview.Localize(c.Name),
                        PluginV3SpatialPreview.Localize(c.Description) + " · " + stateNames[c.Kind] + "\n" + string.Join(" · ", c.Components.Select(id => description.Categories.FirstOrDefault(k => k.Id == id) is { } category ? PluginV3SpatialPreview.Localize(category.Name) : id)), c.Kind.ToString(), false, c.Location is null ? [] : new[] { c.Location }) { Components = c.Components.ToArray(), SourcePaths = c.SourcePaths.ToArray() }), allowReplace: false);
                    Volatile.Write(ref _scanProgress, new(cancellation, page.ScannedFiles, total));
                    if (Interlocked.Exchange(ref _scanRefreshQueued, 1) == 0) EnqueueOnUiThread(() =>
                    {
                        Interlocked.Exchange(ref _scanRefreshQueued, 0);
                        var progress = Volatile.Read(ref _scanProgress);
                        if (!_active || progress is null || progress.Scan.IsCancellationRequested
                            || !ReferenceEquals(progress.Scan, _scanCancellation)) return;
                        var first = !_scanHasRequestedRows; _scanHasRequestedRows = true;
                        TaskObserver.Observe(QueueRowsRefresh(first), "Spatial scan rows");
                    });
                }
                await UiDispatcherService.RunOnUiAsync(async () =>
                {
                    if (!_active || token.IsCancellationRequested) return;
                    _scanCompleted = true;
                    await ReloadRowsAsync();
                    if (_rowRefresh is not null) await _rowRefresh.DrainAsync(token);
                    if (_active && !token.IsCancellationRequested && _rowsPublishedRevision == _rowsRevision)
                    { Status = I18n.GetString("SpatialWorkspace_Complete"); Notify(); }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { EnqueueOnUiThread(() => { if (_active) { Error = ex.Message; Status = I18n.GetString("SpatialWorkspace_Failed"); Notify(); } }); }
            finally { EnqueueOnUiThread(() => { if (_active && ReferenceEquals(_scanCancellation, cancellation)) { IsScanning = false; Notify(); } }); }
        }, CancellationToken.None);
    }
    private async Task StopComparisonAsync()
    {
        _scanCancellation?.Cancel(); await _scan;
        _scanCancellation?.Dispose(); _scanCancellation = null; IsScanning = false;
    }
    public async Task CancelComparisonAsync()
    {
        _scanCompleted = false;
        _canceledMergeSession = Operations?.Snapshot.Session; _mergeRefreshPending = false;
        _operationCancellation?.Cancel(); Operations?.Tracker.Stop(); await StopComparisonAsync();
        if (!_active) return;
        await ReloadRowsAsync(); Status = I18n.GetString(IsMerge ? "SpatialWorkspace_Canceled" : "SpatialWorkspace_PartialScan"); Notify();
    }
    private static SpatialWorkspaceRow MergeRow(MergeChangeItem item)
        => SpatialMergeRows.Create(new(item.Conflict, item.Resolution, item.IsAutomatic), PluginV3SpatialPreview.Localize, I18n.GetString);
    public Task ReloadRowsAsync(bool reset = false)
    {
        if (_index is null || !_active || IsScope || _replacingIndex) return Task.CompletedTask;
        if (reset) _offset = 0;
        ++_rowsRevision;
        return QueueRowsRefresh(true);
    }
    private void InvalidateRows() { ++_rowsRevision; _rowRefresh?.CancelPending(); }
    private Task QueueRowsRefresh(bool immediate)
    {
        if (_index is null || !_active || IsScope || _replacingIndex) return Task.CompletedTask;
        _rowRefresh ??= new(ReadRowsAsync);
        return _rowRefresh.RequestAsync(new(_index, CurrentQuery(), _offset, IsMerge, Category == "$default", _rowsRevision), immediate);
    }
    private async Task ReadRowsAsync(RowRequest request, CancellationToken token)
    {
        try
        {
            var result = await Task.Run(() => request.Index.ReadPresentation(request.Query, request.Offset, request.Merge, token), token).ConfigureAwait(false);
            var diagnostics = request.Index.Diagnostics;
            var queuedAt = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            await UiDispatcherService.RunOnUiAsync(() =>
            {
                var startedAt = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                var published = false;
                try
                {
                if (!_active || token.IsCancellationRequested || request.Revision != _rowsRevision || !ReferenceEquals(request.Index, _index)) return;
                var selected = SelectedItem;
                ((BatchObservableCollection<SpatialWorkspaceItem>)Items).ReplaceAll(result.Rows.Take(100).Select(Item));
                if (selected is not null) SelectedItem = Items.FirstOrDefault(i => i.Id == selected.Id) ?? selected;
                CanNextPage = result.Rows.Count > 100;
                Counts = request.Merge ? I18n.Format("SpatialWorkspace_MergeCounts", result.Total, result.Global.Selected, request.Offset / 100 + 1)
                    : I18n.Format("SpatialWorkspace_CompareCounts", result.Total, request.Offset / 100 + 1);
                if (result.WholeDomain > 0) Counts += " · " + I18n.Format("SpatialWorkspace_WholeDomainCount", result.WholeDomain);
                CategoryBreakdown = string.Join(" · ", result.Categories.Select(c => CategoryName(c.Key) + " " + c.Value));
                CategorySummary = request.Merge ? I18n.Format("SpatialWorkspace_MergeSummary", result.MergeCounts[0], result.MergeCounts[1], result.MergeCounts[2])
                    + (result.MergeCounts[3] + result.MergeCounts[4] > 0 ? "\n" + I18n.Format("SpatialWorkspace_LockedSummary", result.MergeCounts[3] + result.MergeCounts[4]) : "")
                    : request.DefaultCategory && result.UnfilteredTotal > result.Total ? I18n.Format("SpatialWorkspace_HiddenRuntimeChanges", result.UnfilteredTotal - result.Total) : "";
                var progress = Volatile.Read(ref _scanProgress);
                if (progress is not null && ReferenceEquals(progress.Scan, _scanCancellation) && !progress.Scan.IsCancellationRequested)
                {
                    if (_scanCompleted && result.Global.Total == progress.Changes) Status = I18n.GetString("SpatialWorkspace_Complete");
                    else if (IsScanning) Status = I18n.Format("SpatialWorkspace_Scanning", progress.Files, progress.Changes);
                }
                _rowsPublishedRevision = request.Revision;
                Notify(); OverlayChanged?.Invoke();
                published = true;
                }
                finally { diagnostics?.RecordUi(request.Revision, result.IndexVersion, queuedAt, startedAt, published); }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await UiDispatcherService.RunOnUiAsync(() =>
            {
                if (!_active || token.IsCancellationRequested || request.Revision != _rowsRevision) return;
                Error = error.Message; Status = I18n.GetString("SpatialWorkspace_Failed"); Notify();
            }).ConfigureAwait(false);
            throw;
        }
    }
    public Task PageAsync(int direction) { _offset = Math.Max(0, _offset + direction * 100); return ReloadRowsAsync(); }
    public Task SelectItemAsync(SpatialWorkspaceItem item, bool additive)
    {
        SelectedItem = item;
        Details = item.Title + " · " + item.Status + "\n" + string.Join("\n", item.Row.Locations.Select(l => (Worlds.FirstOrDefault(w => w.Id == l.WorldId)?.Name ?? l.WorldId) + " · " + (l.LayerId is null ? I18n.GetString("SpatialWorkspace_WholeWorld") : Layers.FirstOrDefault(v => v.Id == l.LayerId)?.Name ?? l.LayerId))) + "\n" + item.Categories + "\n" + item.Description
            + "\n" + string.Join("\n", item.Row.SourcePaths);
        Notify(); OverlayChanged?.Invoke(); return Task.CompletedTask;
    }
    public void LocateSelected() { if (SelectedItem?.Row.Locations.FirstOrDefault() is { } location) LocateRequested?.Invoke(location); }
    public async Task CheckItemAsync(SpatialWorkspaceItem item, bool value)
    {
        if (!IsMerge || !CanDecide || !item.CanCheck) return;
        var result = await AccessIndexAsync((index, _, _) => { index.ToggleItem(item.Id, value); return true; });
        if (IsCurrent(result, requireRowsRevision: false)) await ReloadRowsAsync();
    }
    public async Task SelectAreaAsync(SpatialPreviewBounds bounds, bool additive, bool remove)
    {
        if (World is null || Layer is null || !CanEdit) return;
        if (IsScope) { await EditScopeAreaAsync(new(World.Id, Layer.Id, bounds), additive, remove); return; }
        if (IsMerge)
        {
            if (!CanDecide) return;
            var query = CurrentQuery(); var onlyUnresolved = BatchOnlyUnresolved;
            var result = await AccessIndexAsync((index, _, _) => index.SelectArea(query, bounds, additive, remove, onlyUnresolved));
            if (!IsCurrent(result, requireRowsRevision: false)) return;
            var summary = result!.Value;
            SelectionFeedback = I18n.Format("SpatialWorkspace_AreaSummary", summary.Unresolved, summary.Decided, summary.Locked)
                + "\n" + I18n.GetString(remove ? "SpatialWorkspace_AreaRemoved" : onlyUnresolved ? "SpatialWorkspace_OnlyUnresolvedHint" : "SpatialWorkspace_IncludeDecidedHint");
        }
        await ReloadRowsAsync();
    }
    public Task<IReadOnlyList<SpatialOverlayRegion>> OverlaysAsync(SpatialPreviewBounds bounds, double scale)
        => OverlaysAsync(bounds, scale, CancellationToken.None);
    internal async Task<IReadOnlyList<SpatialOverlayRegion>> OverlaysAsync(SpatialPreviewBounds bounds, double scale,
        CancellationToken cancellationToken)
    {
        if (World is null || Layer is null || _index is null || !_active || _replacingIndex) return [];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        using var readLease = _indexReads.Enter(cancellation.Token);
        var token = readLease.Token;
        token.ThrowIfCancellationRequested();
        var index = _index;
        var world = World.Id; var layer = Layer.Id;
        if (IsScope) return _scopeSelections.Select((s, i) => (Selection: s, Id: "scope-" + i)).Where(p => p.Selection.WorldId == world && p.Selection.LayerId == layer)
            .Select(p => new SpatialOverlayRegion(p.Selection.Bounds, "Selected") { Ids = [p.Id], Highlighted = SelectedItem?.Id == p.Id }).ToArray();
        var query = CurrentQuery(); var grid = CurrentLayer()?.MinorGridSize ?? 1;
        var focused = SelectedItem?.Row;
        var selected = focused is null ? [] : focused.MemberIds.Length == 0 ? new[] { focused.Id } : focused.MemberIds;
        var read = await Task.Run(() =>
        {
            var regions = index.Overlays(query, bounds, scale, grid, out var version, token)
                .Select(r => new SpatialOverlayRegion(r.Bounds, r.Status, r.Count) { Aggregate = r.Aggregate, States = r.States ?? [],
                    Ids = r.Ids ?? [], Components = (r.Components ?? []).Select(CategoryName).ToArray(), Checked = r.Checked, Highlighted = (r.Ids ?? []).Intersect(selected).Any() }).ToList();
            return (Overlays: regions, Version: version);
        }, token);
        token.ThrowIfCancellationRequested();
        var overlays = read.Overlays; _lastOverlayReadVersion = read.Version;
        if (focused is not null && !overlays.Any(o => o.Highlighted))
            overlays.AddRange(focused.Locations.Where(l => l.WorldId == world && (l.LayerId is null || l.LayerId == layer) && l.Bounds is not null)
                .Select(l => new SpatialOverlayRegion(l.Bounds!, focused.Status) { Highlighted = true, Ids = selected, States = focused.States }));
        return overlays;
    }
    internal async Task<SpatialDecisionSelection?> CaptureDecisionAsync()
    {
        if (!IsMerge || !CanDecide || Operations is not { } operations) return null;
        var result = await AccessIndexAsync((index, captured, token) =>
        {
            var session = captured!; var ids = index.Selected();
            var conflicts = ids.Select(id =>
            {
                token.ThrowIfCancellationRequested();
                return operations.Runtime!.MergeSessions.GetConflict(session, id);
            }).ToArray();
            return ids.Length == 0 ? null : new SpatialDecisionSelection(session.Id, session.Plan.Revision, session.Revision, ids,
                conflicts.Any(c => c.Conflict.SemanticSubject?.SpatialLocations.Any(l => l.Bounds is null) == true),
                conflicts.Count(c => c.Resolution is not null));
        });
        return IsCurrent(result) ? result!.Value : null;
    }
    internal Task DecideAsync(MergeResolutionChoice? choice, SpatialDecisionSelection? expected = null) => !CanDecide ? Task.CompletedTask : RunAsync(async token =>
    {
        if (Operations is not { } operations) return;
        var selection = await AccessIndexAsync((index, session, _) => (Ids: index.Selected(), Session: session!), token);
        if (!IsCurrent(selection)) return;
        var (ids, session) = selection!.Value;
        using var provider = session.Plan.SemanticProvider is { } binding ? await new PluginV3SemanticMerge().AcquireAsync(binding, token) : null;
        RequireSelectionCurrent();
        if (expected is not null && (expected.SessionId != session.Id || expected.PlanRevision != session.Plan.Revision || expected.Revision != session.Revision || !ids.SequenceEqual(expected.Ids)))
            throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_SelectionChanged"));
        if (ids.Length == 0) return;
        if (choice is null)
        {
            RequireSelectionCurrent();
            await operations.ClearAsync(ids);
        }
        else
        {
            var decisions = await Task.Run(() => ids.Select(id =>
            {
                token.ThrowIfCancellationRequested();
                var item = operations.Runtime!.MergeSessions.GetConflict(session, id).Conflict;
                if (item.SemanticSubject is not { } subject || !MergeSessionStore.SemanticChoiceAllowed(subject, choice.Value))
                    throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_ChoiceUnavailable"));
                return new MergeResolution(session.Plan.Revision, id, item.InputSignature, choice.Value);
            }).ToArray(), token);
            RequireSelectionCurrent();
            await operations.ResolveAsync(decisions);
        }
        if (operations.Snapshot.Error is { Length: > 0 } error) throw new InvalidOperationException(error);
        await SynchronizeMergeIndexAsync(token);
        SelectionFeedback = I18n.Format("SpatialWorkspace_DecisionsApplied", ids.Length);
        await ReloadRowsAsync();
        void RequireSelectionCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(selection)) throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_SelectionChanged"));
        }
    });
    public Task UndoAsync() => RunAsync(async token => { await Operations!.UndoAsync(); await SynchronizeMergeIndexAsync(token); await ReloadRowsAsync(); });
    public Task GenerateResultAsync() => RunAsync(async token =>
    {
        await ClosePanesAsync(); _inputs.Remove("result");
        await Operations!.GenerateReviewAsync();
        if (Operations.Review is null) throw new InvalidOperationException(Operations.Snapshot.Error ?? I18n.GetString("SpatialWorkspace_ResultExpired"));
        var choice = Inputs.FirstOrDefault(c => c.Id == "result");
        if (choice is null) { choice = new("result", I18n.GetString("SpatialWorkspace_Result")); Inputs.Add(choice); }
        Left = Inputs.Single(c => c.Id == "ours"); Right = choice; ChoicesChanged?.Invoke();
        await OpenPanesAsync(token);
    });
    public async Task NextUnresolvedAsync()
    {
        if (!CanDecide) return;
        var after = SelectedItem?.Id;
        var result = await AccessIndexAsync<(MergeConflict Conflict, MergeResolution? Resolution)?>((_, captured, token) =>
        {
            var session = captured!;
            var id = Operations!.Runtime!.MergeSessions.NextSemanticConflict(session, "", after);
            token.ThrowIfCancellationRequested();
            return id is null ? null : Operations.Runtime.MergeSessions.GetConflict(session, id);
        });
        if (!IsCurrent(result) || result!.Value is not { } row) return;
        await SelectItemAsync(new(MergeRow(new(row.Conflict, row.Resolution))), false);
        LocateSelected();
    }
}
