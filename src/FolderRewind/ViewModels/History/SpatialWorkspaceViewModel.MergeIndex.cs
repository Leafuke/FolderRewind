using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class SpatialWorkspaceViewModel
{
    private SpatialMergeIndexBuild? _mergeIndexBuild;
    private SpatialIndexReadLifetime _indexReads = new();
    private string? _mergeIndexRoot;
    private long _mergeIndexGeneration;
    private CancellationTokenSource? _mergeBuildCancellation;
    private MergeSession? _buildingMergeSession;
    private long _indexedRevision = -1;
    private Guid _indexedSession;
    private bool _replacingIndex, _mergeRefreshPending, _mergeProgressStatus;
    private MergeSession? _canceledMergeSession;
    public event Action? IndexChanging;
    private sealed record IndexRequest(SpatialWorkspaceIndex Index, MergeSession? Session, long Generation, long RowsRevision);
    private sealed record IndexResult<T>(IndexRequest Request, T Value);
    private bool IsCurrent<T>(IndexResult<T>? result, bool requireRowsRevision = true) => result is not null && _active && !_replacingIndex
        && ReferenceEquals(result.Request.Index, _index) && result.Request.Generation == _mergeIndexGeneration
        && (!requireRowsRevision || result.Request.RowsRevision == _rowsRevision)
        && (!IsMerge || IndexMatches(result.Request.Session) && Operations?.Snapshot is { IsBusy: false }
            && SameRevision(result.Request.Session, Operations.Snapshot.Session));
    private async Task<IndexResult<T>?> AccessIndexAsync<T>(Func<SpatialWorkspaceIndex, MergeSession?, CancellationToken, T> read,
        CancellationToken token = default)
    {
        if (_index is null || !_active || _replacingIndex) return null;
        if (IsMerge && !IndexMatches(Operations?.Snapshot.Session)) return null;
        using var lease = _indexReads.Enter(token);
        var request = new IndexRequest(_index, Operations?.Snapshot.Session, _mergeIndexGeneration, _rowsRevision);
        try
        {
            var value = await Task.Run(() => { lease.Token.ThrowIfCancellationRequested(); return read(request.Index, request.Session, lease.Token); }, lease.Token);
            lease.Token.ThrowIfCancellationRequested();
            return new(request, value);
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested) { return null; }
    }
    private void RaiseIndexChanging()
    {
        foreach (var observer in IndexChanging?.GetInvocationList() ?? [])
            try { ((Action)observer)(); } catch { /* Observers cannot strand the index lifetime. */ }
    }

    private static bool SameRevision(MergeSession? left, MergeSession? right) => left is not null && right is not null
        && left.Id == right.Id && left.Plan.Revision == right.Plan.Revision && left.Revision == right.Revision && left.State == right.State;
    private bool IndexMatches(MergeSession? session) => session is not null && session.Id == _indexedSession
        && session.Plan.Revision == _indexedPlan && session.Revision == _indexedRevision;
    private bool CanBuildIndex => Operations?.Snapshot is { IsBusy: false, Session.State: MergeSessionState.Ready or MergeSessionState.Resolving };

    private void RequestMergeIndexRefresh()
    {
        if (!IsMerge || !_active || !CanBuildIndex || IndexMatches(Operations!.Snapshot.Session)
            || SameRevision(_canceledMergeSession, Operations.Snapshot.Session)) return;
        if (SameRevision(_buildingMergeSession, Operations.Snapshot.Session)) return;
        _mergeRefreshPending = true; _mergeBuildCancellation?.Cancel();
        if (!IsBusy) TaskObserver.Observe(RunAsync(async token =>
        {
            var plan = _indexedPlan;
            await SynchronizeMergeIndexAsync(token);
            if (_active && IndexMatches(Operations.Snapshot.Session))
            {
                if (plan != _indexedPlan || LeftSession is null)
                { _inputs.Clear(); await ClosePanesAsync(); await OpenPanesAsync(token); SetCategories(new([])); }
                await ReloadRowsAsync();
            }
        }), "Spatial Merge index refresh");
    }

    private async Task SynchronizeMergeIndexAsync(CancellationToken token)
    {
        while (_active && CanBuildIndex && !IndexMatches(Operations!.Snapshot.Session))
        {
            token.ThrowIfCancellationRequested();
            var state = Operations.Snapshot;
            if (_mergeIndexBuild is not { } published || !ReferenceEquals(_index, published.Index)
                || !SpatialMergeIndexUpdater.CanApply(published.Snapshot, state.DecisionChange, state.Session))
            { await RefreshMergeIndexAsync(token); return; }
            try { await UpdateMergeIndexAsync(published, state.DecisionChange!, token); }
            catch (MergeChangeSnapshotStaleException)
            {
                if (!await RefreshStaleMergeSessionAsync(state.Session!, token)) return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested) { }
            // A newly arrived contiguous decision can update this same owner again.
            // A missing transition or changed plan takes the full-snapshot path above.
        }
    }

    private async Task<bool> RefreshStaleMergeSessionAsync(MergeSession expected, CancellationToken token)
    {
        if (!SameRevision(expected, Operations!.Snapshot.Session)) return true;
        // A page must not wait for a long application-owned reanalysis just to retire its index.
        if (Operations.Snapshot.IsBusy) { _mergeRefreshPending = true; return false; }
        await Operations.LoadAsync(expected.Id).WaitAsync(token);
        if (SameRevision(expected, Operations.Snapshot.Session)) throw new MergeChangeSnapshotStaleException();
        return true;
    }

    private async Task UpdateMergeIndexAsync(SpatialMergeIndexBuild published, MergeDecisionChange change, CancellationToken token)
    {
        var generation = ++_mergeIndexGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _mergeBuildCancellation = cancellation; _buildingMergeSession = change.After;
        _mergeRefreshPending = false; _replacingIndex = true;
        var reads = _indexReads;
        try
        {
            InvalidateRows(); RaiseIndexChanging();
            await reads.StopAsync();
            if (_rowRefresh is not null) await _rowRefresh.DrainAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            await Task.Run(() => SpatialMergeIndexUpdater.Apply(Operations!.Runtime!.MergeSessions, published, change,
                row => SpatialMergeRows.Create(row, PluginV3SpatialPreview.Localize, I18n.GetString), cancellation.Token), cancellation.Token);
            // Apply has committed one complete revision. Record it even if a newer operation
            // arrived while this continuation was queued; it must not be labelled as the old revision.
            _indexedSession = published.Snapshot.SessionId; _indexedPlan = published.Snapshot.PlanRevision;
            _indexedRevision = published.Snapshot.Revision;
        }
        finally
        {
            try { await reads.DisposeAsync(); }
            finally
            {
                _indexReads = new(); _replacingIndex = false;
                if (ReferenceEquals(_mergeBuildCancellation, cancellation)) { _mergeBuildCancellation = null; _buildingMergeSession = null; }
                if (_active) OverlayChanged?.Invoke();
            }
        }
        bool Current() => _active && generation == _mergeIndexGeneration && ReferenceEquals(_mergeIndexBuild, published)
            && ReferenceEquals(_index, published.Index) && CanBuildIndex
            && SpatialMergeIndexUpdater.CanApply(published.Snapshot, change, Operations!.Snapshot.Session);
    }

    private async Task RefreshMergeIndexAsync(CancellationToken token)
    {
        _canceledMergeSession = null;
        while (_active && CanBuildIndex)
        {
            token.ThrowIfCancellationRequested();
            _mergeRefreshPending = false;
            var session = Operations!.Snapshot.Session!;
            var generation = ++_mergeIndexGeneration;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            _mergeBuildCancellation = cancellation; _buildingMergeSession = session;
            var progress = new MergePreparationReporter(value => EnqueueOnUiThread(() =>
            {
                if (!_active || generation != _mergeIndexGeneration || cancellation.IsCancellationRequested
                    || !ReferenceEquals(_mergeBuildCancellation, cancellation)) return;
                _mergeProgressStatus = true; Status = MergeProgressPresentation.Text(value); Notify();
            })) { SessionId = session.Id, PlanRevision = session.Plan.Revision };
            progress.Report(MergePreparationStage.InitializingVisualization);
            SpatialMergeIndexBuild? candidate = null;
            try
            {
#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
                await Services.Development.NativeArtifactAcceptance.PauseUiFixtureAsync("merge-spatial-index", cancellation.Token);
#endif
                candidate = await Task.Run(() => SpatialMergeIndexBuilder.Build(Operations.Runtime!.MergeSessions, session, _mergeIndexRoot!,
                    row => SpatialMergeRows.Create(row, PluginV3SpatialPreview.Localize, I18n.GetString), cancellation.Token,
                    (written, total) => progress.Report(MergePreparationStage.InitializingVisualization, written, total)), cancellation.Token);
                if (!CanPublish()) continue;

                _replacingIndex = true;
                var reads = _indexReads;
                try
                {
                    InvalidateRows(); RaiseIndexChanging();
                    await reads.StopAsync();
                    if (_rowRefresh is not null) await _rowRefresh.DrainAsync();
                    token.ThrowIfCancellationRequested();
                    var snapshot = candidate.Snapshot;
                    await Task.Run(() => SpatialMergeIndexBuilder.RequireCurrent(Operations.Runtime!.MergeSessions, session, snapshot), cancellation.Token);
                    if (!CanPublish()) continue;
                    var priorBuild = _mergeIndexBuild; var priorIndex = _index;
                    var planChanged = _indexedPlan != session.Plan.Revision;
                    _mergeIndexBuild = candidate; _index = candidate.Index; candidate = null;
                    _indexedSession = session.Id; _indexedPlan = session.Plan.Revision; _indexedRevision = session.Revision;
                    if (Error == I18n.GetString("SpatialWorkspace_PlanExpired")) Error = "";
                    if (planChanged) { SelectedItem = null; Details = ""; }
                    try
                    {
                        if (priorBuild is not null) await Task.Run(priorBuild.Dispose);
                        else if (priorIndex is not null) await Task.Run(priorIndex.Dispose);
                    }
                    catch (Exception error) { LogService.LogWarning(error.ToString(), "Spatial previous index cleanup"); }
                }
                finally
                {
                    try { await reads.DisposeAsync(); }
                    finally
                    {
                        _indexReads = new(); _replacingIndex = false;
                        if (_active) OverlayChanged?.Invoke();
                    }
                }
                if (IndexMatches(Operations.Snapshot.Session)) return;
            }
            catch (MergeChangeSnapshotStaleException)
            {
                if (!await RefreshStaleMergeSessionAsync(session, token)) return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested) { }
            finally
            {
                try { if (candidate is not null) await Task.Run(candidate.Dispose); }
                finally
                {
                    if (ReferenceEquals(_mergeBuildCancellation, cancellation)) { _mergeBuildCancellation = null; _buildingMergeSession = null; }
                }
            }
            if (!CanBuildIndex) return;
            bool CanPublish() => _active && !cancellation.IsCancellationRequested && generation == _mergeIndexGeneration
                && CanBuildIndex && SameRevision(session, Operations.Snapshot.Session);
        }
    }
}
