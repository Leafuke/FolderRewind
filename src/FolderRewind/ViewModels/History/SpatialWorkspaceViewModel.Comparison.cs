using FolderRewind.Controls;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

internal sealed record SpatialDecisionSelection(Guid SessionId, Guid PlanRevision, long Revision, string[] Ids, bool WholeDomain, int PreviouslyDecided);

public sealed partial class SpatialWorkspaceViewModel
{
    private void StartComparison()
    {
        if (LeftSession is null || RightSession is null) return;
        var stateNames = Enum.GetValues<SpatialChangeKind>().ToDictionary(k => k, k => I18n.GetString("SpatialWorkspace_State_" + k));
        var initial = new SpatialComparisonRequest(Guid.NewGuid(), LeftSession.Source, RightSession.Source, null, 500)
        { InitialPriority = World is null ? null : new SpatialSubjectLocation(World.Id, Layer?.Id, _visibleBounds) };
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _scanCancellation; var token = cancellation.Token; IsScanning = true;
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
                    _index!.Put(page.Changes.Select(c => new SpatialWorkspaceRow(c.Id, PluginV3SpatialPreview.Localize(c.Name),
                        PluginV3SpatialPreview.Localize(c.Description) + " · " + stateNames[c.Kind] + "\n" + string.Join(" · ", c.Components.Select(id => description.Categories.FirstOrDefault(k => k.Id == id) is { } category ? PluginV3SpatialPreview.Localize(category.Name) : id)), c.Kind.ToString(), false, c.Location is null ? [] : new[] { c.Location }) { Components = c.Components.ToArray(), SourcePaths = c.SourcePaths.ToArray() }), allowReplace: false);
                    await UiDispatcherService.RunOnUiAsync(async () =>
                    {
                        if (!_active || token.IsCancellationRequested) return;
                        Status = I18n.Format("SpatialWorkspace_Scanning", page.ScannedFiles, total); await ReloadRowsAsync();
                    });
                }
                EnqueueOnUiThread(() => { if (_active && !token.IsCancellationRequested) { Status = I18n.GetString("SpatialWorkspace_Complete"); Notify(); } });
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
    { _operationCancellation?.Cancel(); Operations?.Tracker.Stop(); await StopComparisonAsync(); Status = I18n.GetString("SpatialWorkspace_PartialScan"); Notify(); }
    private static SpatialWorkspaceRow MergeRow(MergeChangeItem item)
    {
        var subject = item.Conflict.SemanticSubject;
        var status = subject?.IsBlocked == true ? "Blocked" : subject is { IsAutomatic: true, CanOverrideAutomatic: false } ? "Locked"
            : item.Resolution is null ? "Unresolved" : item.Resolution.Choice == MergeResolutionChoice.Automatic ? "Automatic" : "Resolved";
        return new(item.Id, subject is null ? item.Path : PluginV3SpatialPreview.Localize(subject.DisplayName), subject is null ? item.Description : PluginV3SpatialPreview.Localize(subject.Description) + "\n" + item.Description, status,
            subject is not null && item.CanCheck, subject?.SpatialLocations ?? []);
    }
    private async Task RefreshMergeIndexAsync(CancellationToken token)
    {
        var session = Operations!.Snapshot.Session!; var store = Operations.Runtime!.MergeSessions;
        await Task.Run(() =>
        {
            _index!.Clear();
            for (var offset = 0; ; offset += 1000)
            {
                token.ThrowIfCancellationRequested();
                var page = store.QuerySemanticChanges(session, "", "All", offset, 1000);
                _index.Put(page.Rows.Select(r => MergeRow(new(r.Conflict, r.Resolution, r.Automatic))));
                if (page.Rows.Count < 1000) break;
            }
        }, token);
    }
    public async Task ReloadRowsAsync(bool reset = false)
    {
        if (_index is null || !_active || _index.IsDisposed || IsScope) return;
        if (reset) _offset = 0;
        var query = CurrentQuery(); var offset = _offset; var revision = ++_rowsRevision;
        var result = await Task.Run(() => (_index.Query(query, offset, 101), _index.Count(query), _index.Counts(), _index.CategoryCounts(query), _index.WholeDomainCount(query), _index.Count(query with { Categories = null }), IsMerge ? new[] { "Automatic", "Resolved", "Unresolved", "Locked", "Blocked" }.Select(status => _index.Count(query with { Status = status })).ToArray() : Array.Empty<int>()));
        if (!_active || revision != _rowsRevision) return;
        Items.Clear(); foreach (var row in result.Item1.Take(100)) Items.Add(Item(row));
        CanNextPage = result.Item1.Count > 100;
        Counts = IsMerge ? I18n.Format("SpatialWorkspace_MergeCounts", result.Item2, result.Item3.Selected, offset / 100 + 1)
            : I18n.Format("SpatialWorkspace_CompareCounts", result.Item2, offset / 100 + 1);
        if (result.Item5 > 0) Counts += " · " + I18n.Format("SpatialWorkspace_WholeDomainCount", result.Item5);
        CategoryBreakdown = string.Join(" · ", result.Item4.Select(c => CategoryName(c.Key) + " " + c.Value));
        CategorySummary = IsMerge ? I18n.Format("SpatialWorkspace_MergeSummary", result.Item7[0], result.Item7[1], result.Item7[2])
            + (result.Item7[3] + result.Item7[4] > 0 ? "\n" + I18n.Format("SpatialWorkspace_LockedSummary", result.Item7[3] + result.Item7[4]) : "")
            : Category == "$default" && result.Item6 > result.Item2 ? I18n.Format("SpatialWorkspace_HiddenRuntimeChanges", result.Item6 - result.Item2) : "";
        Notify(); OverlayChanged?.Invoke();
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
        await Task.Run(() => _index!.ToggleItem(item.Id, value)); await ReloadRowsAsync();
    }
    public async Task SelectAreaAsync(SpatialPreviewBounds bounds, bool additive, bool remove)
    {
        if (World is null || Layer is null || !CanEdit) return;
        if (IsScope) { await EditScopeAreaAsync(new(World.Id, Layer.Id, bounds), additive, remove); return; }
        if (IsMerge)
        {
            if (!CanDecide) return;
            var query = CurrentQuery(); var onlyUnresolved = BatchOnlyUnresolved;
            var summary = await Task.Run(() => _index!.SelectArea(query, bounds, additive, remove, onlyUnresolved));
            SelectionFeedback = I18n.Format("SpatialWorkspace_AreaSummary", summary.Unresolved, summary.Decided, summary.Locked)
                + "\n" + I18n.GetString(remove ? "SpatialWorkspace_AreaRemoved" : onlyUnresolved ? "SpatialWorkspace_OnlyUnresolvedHint" : "SpatialWorkspace_IncludeDecidedHint");
        }
        await ReloadRowsAsync();
    }
    public async Task<IReadOnlyList<SpatialOverlayRegion>> OverlaysAsync(SpatialPreviewBounds bounds, double scale)
    {
        if (World is null || Layer is null || _index is null || !_active) return [];
        var world = World.Id; var layer = Layer.Id;
        if (IsScope) return _scopeSelections.Select((s, i) => (Selection: s, Id: "scope-" + i)).Where(p => p.Selection.WorldId == world && p.Selection.LayerId == layer)
            .Select(p => new SpatialOverlayRegion(p.Selection.Bounds, "Selected") { Ids = [p.Id], Highlighted = SelectedItem?.Id == p.Id }).ToArray();
        var query = CurrentQuery(); var grid = CurrentLayer()?.MinorGridSize ?? 1;
        var focused = SelectedItem?.Row;
        var selected = focused is null ? [] : focused.MemberIds.Length == 0 ? new[] { focused.Id } : focused.MemberIds;
        var overlays = await Task.Run(() => _index.Overlays(query, bounds, scale, grid)
            .Select(r => new SpatialOverlayRegion(r.Bounds, r.Status, r.Count) { Aggregate = r.Aggregate, States = r.States ?? [],
                Ids = r.Ids ?? [], Components = (r.Components ?? []).Select(CategoryName).ToArray(), Checked = r.Checked, Highlighted = (r.Ids ?? []).Intersect(selected).Any() }).ToList());
        if (focused is not null && !overlays.Any(o => o.Highlighted))
            overlays.AddRange(focused.Locations.Where(l => l.WorldId == world && (l.LayerId is null || l.LayerId == layer) && l.Bounds is not null)
                .Select(l => new SpatialOverlayRegion(l.Bounds!, focused.Status) { Highlighted = true, Ids = selected, States = focused.States }));
        return overlays;
    }
    internal async Task<SpatialDecisionSelection?> CaptureDecisionAsync()
    {
        if (!IsMerge || !CanDecide) return null;
        var session = Operations!.Snapshot.Session!;
        return await Task.Run(() =>
        {
            var ids = _index!.Selected();
            var conflicts = ids.Select(id => Operations.Runtime!.MergeSessions.GetConflict(session, id)).ToArray();
            return ids.Length == 0 ? null : new SpatialDecisionSelection(session.Id, session.Plan.Revision, session.Revision, ids,
                conflicts.Any(c => c.Conflict.SemanticSubject?.SpatialLocations.Any(l => l.Bounds is null) == true),
                conflicts.Count(c => c.Resolution is not null));
        });
    }
    internal Task DecideAsync(MergeResolutionChoice? choice, SpatialDecisionSelection? expected = null) => RunAsync(async token =>
    {
        var session = Operations!.Snapshot.Session!;
        var ids = await Task.Run(() => _index!.Selected(), token);
        using var provider = session.Plan.SemanticProvider is { } binding ? await new PluginV3SemanticMerge().AcquireAsync(binding, token) : null;
        if (expected is not null && (expected.SessionId != session.Id || expected.PlanRevision != session.Plan.Revision || expected.Revision != session.Revision || !ids.SequenceEqual(expected.Ids)))
            throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_SelectionChanged"));
        if (ids.Length == 0) return;
        if (choice is null) await Operations.ClearAsync(ids);
        else
        {
            var decisions = await Task.Run(() => ids.Select(id =>
            {
                var item = Operations.Runtime!.MergeSessions.GetConflict(session, id).Conflict;
                if (item.SemanticSubject is not { } subject || !MergeSessionStore.SemanticChoiceAllowed(subject, choice.Value))
                    throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_ChoiceUnavailable"));
                return new MergeResolution(session.Plan.Revision, id, item.InputSignature, choice.Value);
            }).ToArray(), token);
            await Operations.ResolveAsync(decisions);
        }
        if (Operations.Snapshot.Error is { Length: > 0 } error) throw new InvalidOperationException(error);
        var current = Operations.Snapshot.Session!;
        await Task.Run(() => _index!.Put(ids.Select(id =>
        {
            var item = Operations.Runtime!.MergeSessions.GetConflict(current, id);
            return MergeRow(new(item.Conflict, item.Resolution, item.Resolution?.Choice == MergeResolutionChoice.Automatic));
        })), token);
        await Task.Run(() => _index!.ClearSelection(), token);
        SelectionFeedback = I18n.Format("SpatialWorkspace_DecisionsApplied", ids.Length);
        await ReloadRowsAsync();
    });
    public Task UndoAsync() => RunAsync(async token => { await Operations!.UndoAsync(); await RefreshMergeIndexAsync(token); await ReloadRowsAsync(); });
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
        if (Operations?.Snapshot.Session is not { } session) return;
        var id = await Task.Run(() => Operations.Runtime!.MergeSessions.NextSemanticConflict(session, "", SelectedItem?.Id));
        if (id is null) return;
        var row = Operations.Runtime!.MergeSessions.GetConflict(session, id);
        await SelectItemAsync(new(MergeRow(new(row.Conflict, row.Resolution))), false);
        LocateSelected();
    }
}
