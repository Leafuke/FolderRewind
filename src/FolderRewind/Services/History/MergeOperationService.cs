using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>One application-owned workspace per configuration/source; one executing merge per configuration.</summary>
internal sealed partial class MergeOperationService
{
    private static readonly ConcurrentDictionary<(string, SourceId), MergeOperationService> Instances = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static bool _stopping;
    public static MergeOperationService Get(BackupConfig config, SourceId source)
        => Instances.GetOrAdd((config.Id, source), _ => new(config, source));
    public static Task[] ActiveTasks => Instances.Values.Select(s => s.Tracker.PendingTask).Where(t => !t.IsCompleted).ToArray();
    public static void BeginShutdown() { _stopping = true; foreach (var item in Instances.Values) item.Tracker.Stop(); }
    public static void CancelShutdown() => _stopping = false;

    private MergeOperationService(BackupConfig config, SourceId source)
    { Config = config; SourceId = source; _configSignature = NativeHistoryConfigLease.Signature(config); }
    public BackupConfig Config { get; private set; }
    private string _configSignature;
    public SourceId SourceId { get; }
    public MergeOperationTracker Tracker { get; } = new();
    public MergeOperationSnapshot Snapshot => Tracker.Snapshot;
    public HistoryRuntime? Runtime { get; private set; }
    public IReadOnlyList<MergeSession> Sessions { get; private set; } = [];
    private HistoryRestoreService? _restore;
    private Func<CancellationToken, Task>? _pendingSave;
    public bool CanRetrySave => !Snapshot.IsBusy && !Snapshot.IsSaved && _pendingSave is not null;
    public Task RetrySaveAsync() => _pendingSave is { } save ? Run(MergeOperationStage.Saving, save) : Task.CompletedTask;
    private HistoryMergeService Core => NativeHistoryApplicationService.MergeCore(Runtime!, _restore!, Snapshot.Session, Tracker.CreateProgressObserver());

    private Task Run(MergeOperationStage stage, Func<CancellationToken, Task> action, bool canStop = true)
    {
        if (_stopping) return Task.CompletedTask;
        return Tracker.RunAsync(stage, async token =>
        {
            using var operation = new NotificationOperationScope(new(NotificationTargetKind.History, ConfigId: Config.Id, FolderId: SourceId.ToString()),
                context: new("", "Merge"));
            using var diagnostic = new BusinessOperationDiagnostics("Merge");
            var previousResult = Snapshot.Result;
            var gate = Gates.GetOrAdd(Config.Id, _ => new(1, 1));
            if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
                throw new InvalidOperationException(I18n.GetString("MergeWorkspace_ConfigBusy"));
            try
            {
                // Refresh inside the tracked operation, never by replacing a service that may still own work or unsaved decisions.
                var (current, signature) = await UiDispatcherService.RunOnUiAsync(() =>
                {
                    var registered = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => c.Id == Config.Id)
                        ?? throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
                    return Task.FromResult((registered, NativeHistoryConfigLease.Signature(registered)));
                }).ConfigureAwait(false);
                if (!ReferenceEquals(Config, current) || signature != _configSignature)
                {
                    Config = current; _configSignature = signature;
                    _restore = null; Review = null; _automatic = []; _automaticPlan = null; _undo = null;
                }
                Runtime ??= await NativeHistoryCoreGateway.EnsureReadyAsync(Config, token).ConfigureAwait(false);
                _restore ??= await NativeHistoryApplicationService.CreateRestoreServiceAsync(Config, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (stage == MergeOperationStage.Saving) { _pendingSave = action; Tracker.Update(s => s with { IsSaved = false }); }
                await action(token).ConfigureAwait(false);
                if (stage == MergeOperationStage.Saving) { _pendingSave = null; Tracker.Update(s => s with { IsSaved = true }); }
                var appliedResult = !ReferenceEquals(previousResult, Snapshot.Result) ? Snapshot.Result : null;
                diagnostic.Complete(appliedResult?.Status.ToString() ?? "Success",
                    appliedResult is { Succeeded: false } ? LogLevel.Error : appliedResult?.PostActionDiagnostic is not null ? LogLevel.Warning : LogLevel.Info,
                    $"stage={stage}; mergeSessionId={Snapshot.Session?.Id}; planRevision={Snapshot.Session?.Plan.Revision}; providerId={Snapshot.Session?.Plan.SemanticProvider?.ProviderId.ToString() ?? "folderrewind.core"}; providerVersion={Snapshot.Session?.Plan.ProviderVersion}; state={Snapshot.Session?.State}");
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested)
            { diagnostic.Failure(error, canceled: true); throw; }
            catch (Exception ex)
            {
                diagnostic.Failure(ex);
                if (stage == MergeOperationStage.Saving && Snapshot.IsSaved) _pendingSave = null;
                try { Refresh(); } catch { /* The original error/result takes precedence over a failed refresh. */ }
                if (ex is HistoryMergeBlockedException blocked)
                {
                    if (blocked.Diagnostic.Code == MergeDiagnosticCode.Stale && Snapshot.Session is { } stale
                        && stale.State is MergeSessionState.Preparing or MergeSessionState.Resolving or MergeSessionState.Ready)
                        SetSession(Runtime!.MergeSessions.Update(stale, MergeSessionState.Stale));
                    throw new InvalidOperationException(I18n.GetString("Merge_Diagnostic_" + blocked.Diagnostic.Code)
                        + "\n" + I18n.GetString(blocked.Diagnostic.NextActionKey), ex);
                }
                throw;
            }
            finally { gate.Release(); }
        }, canStop);
    }

    private void SetSession(MergeSession? session, bool decisionsSaved = false, MergeDecisionChange? decisionChange = null)
    {
        if (Review is { } r && (r.SessionId != session?.Id || r.ResolutionRevision != session.Revision)) Review = null;
        if (session?.State is MergeSessionState.Committed or MergeSessionState.Abandoned)
        { _automatic = []; _automaticPlan = null; _undo = null; }
        Tracker.Update(s => s with { Session = session, IsSaved = decisionsSaved || s.IsSaved || _pendingSave is null,
            DecisionChange = decisionChange ?? (s.DecisionChange?.Matches(session) == true ? s.DecisionChange : null) });
    }
    private void Refresh()
    {
        // Result is already captured before this potentially failing read.
        if (Snapshot.Session is { } session)
        {
            var current = Runtime!.MergeSessions.Load(session.Id);
            if (current.State is MergeSessionState.Preparing or MergeSessionState.Resolving or MergeSessionState.Ready
                && (current.Plan.ConfigRevision != _configSignature
                    || !NativeHistoryApplicationService.MergeBindingCurrent(current)))
                current = Runtime.MergeSessions.Update(current, MergeSessionState.Stale);
            SetSession(current);
        }
        Sessions = Runtime!.MergeSessions.List().Where(s => s.Plan.Ours.SourceId == SourceId).ToArray();
        Tracker.Update(s => s);
    }
    private MergeSession RequireSession() => Snapshot.Session ?? throw new InvalidOperationException(I18n.GetString("Merge_SelectBranch"));

    public Task LoadAsync(Guid? id = null) => Run(MergeOperationStage.Loading, async token =>
    {
        await _restore!.RecoverIncompleteAsync(token).ConfigureAwait(false);
        if (id is { } selected)
        {
            if (_pendingSave is not null && Snapshot.Session?.Id != selected) throw new InvalidOperationException(I18n.GetString("MergeWorkspace_Unsaved"));
            var session = Runtime!.MergeSessions.Load(selected);
            if (session.Plan.Ours.SourceId != SourceId) throw new InvalidOperationException("Merge source identity mismatch.");
            if (Snapshot.Session?.Id != id) Tracker.Update(s => s with { Result = null });
            SetSession(session);
        }
        Refresh();
        NormalizeRecoveredResult();
    });

    public Task StartAsync(BranchId source, SemanticMergeBinding? binding = null) => Run(MergeOperationStage.Analyzing, async token =>
    {
        if (_pendingSave is not null) throw new InvalidOperationException(I18n.GetString("MergeWorkspace_Unsaved"));
        Review = null; _undo = null; RecomputeSummary = null;
        Tracker.Update(s => s with { Result = null, Session = null, DecisionChange = null });
        var progress = Tracker.CreateProgressObserver(); progress(new(MergePreparationStage.PreparingHistory));
#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
        await Development.NativeArtifactAcceptance.PauseUiFixtureAsync("merge-history", token).ConfigureAwait(false);
#endif
        SetSession(await NativeHistoryApplicationService.StartMergeAsync(Config, source, token, binding, progress).ConfigureAwait(false));
        if (Snapshot.Session is null) Tracker.Update(s => s with { Notice = "Merge_NoOp" });
        Refresh();
    });
    public Task RecomputeAsync() => Run(MergeOperationStage.Analyzing, async token =>
    {
        var before = Runtime!.MergeSessions.ConflictCounts(RequireSession()).Resolved;
        SetSession(await NativeHistoryApplicationService.RecomputeMergeAsync(Config, RequireSession(), token, Tracker.CreateProgressObserver()).ConfigureAwait(false));
        var retained = Runtime.MergeSessions.ConflictCounts(RequireSession()).Resolved;
        Tracker.Update(s => s with { Result = null });
        RecomputeSummary = string.Format(I18n.GetString("MergeWorkspace_Recomputed"), retained, Math.Max(0, before - retained));
        Refresh();
    });
    public Task PrepareReplicasAsync() => Run(MergeOperationStage.Downloading, async token =>
    {
        SetSession(await NativeHistoryApplicationService.PrepareMergeReplicasAsync(Config, RequireSession(), token, Tracker.CreateProgressObserver()).ConfigureAwait(false));
        Refresh();
    });
    public Task ResumeAsync() => Run(MergeOperationStage.Recovering, async token =>
    {
        await _restore!.RecoverIncompleteAsync(token).ConfigureAwait(false);
        var session = Runtime!.MergeSessions.Load(RequireSession().Id);
        if (session.State == MergeSessionState.Preparing) session = await Core.PrepareAsync(session, token).ConfigureAwait(false);
        SetSession(session); Refresh(); NormalizeRecoveredResult();
    }, false);
    public Task AbandonAsync() => Run(MergeOperationStage.Finishing, async token =>
    {
        await using var lease = await Runtime!.MutationGate.EnterAsync(token).ConfigureAwait(false);
        SetSession(Runtime.MergeSessions.Update(RequireSession(), MergeSessionState.Abandoned));
        _pendingSave = null;
        var catalog = (await Runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (catalog is not null) Runtime.MergeSessions.CleanupTerminalArtifacts(catalog);
        Refresh();
    }, false);
    public Task ResolveAsync(IReadOnlyList<MergeResolution> decisions) => Run(MergeOperationStage.Saving, _ =>
    {
        var original = RequireSession(); var before = ReadPrevious(original, decisions.Select(d => d.ConflictId));
        var session = Runtime!.MergeSessions.ResolveBatch(original, decisions);
        Remember(session, before); SetSession(session, true, new(original, session, before.Keys.ToImmutableArray())); Refresh(); return Task.CompletedTask;
    });
    public string? RecomputeSummary { get; private set; }
    public MergeReviewSnapshot? Review { get; private set; }
    public Task GenerateReviewAsync() => Run(MergeOperationStage.Building, async token =>
    {
        Review = null; Tracker.Update(s => s with { Result = null });
        Review = await NativeHistoryApplicationService.PrepareMergeReviewAsync(Config, RequireSession(), Tracker.ReportStage, token).ConfigureAwait(false);
        Refresh();
    });
    public void DismissReview() { if (!Snapshot.IsBusy) { Review = null; Tracker.Update(s => s); } }
    public Task ApplyReviewedAsync() => Run(MergeOperationStage.Validating, async token =>
    {
        var review = Review ?? throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
        var result = await NativeHistoryApplicationService.ApplyMergeAsync(Config, RequireSession(), token, review, Tracker.ReportStage, Tracker.EnterCritical).ConfigureAwait(false);
        Tracker.ReportResult(result);
        if (!result.TargetCommitted) Review = null;
        Refresh();
    });
    public Task ImportAsync(MergeConflict conflict, string path) => Run(MergeOperationStage.Saving, async token =>
    {
        var original = RequireSession();
        var session = await Core.ImportManualAsync(original, conflict, path, token).ConfigureAwait(false);
        SetSession(session, true, new(original, session, [conflict.Id])); Refresh();
    });
    private void NormalizeRecoveredResult()
    {
        if (Snapshot.Session?.State == MergeSessionState.Committed && Snapshot.Result?.Status == HistoryRestoreStatus.CommittedRecoveryRequired)
            Tracker.ReportResult(Snapshot.Result with { Status = HistoryRestoreStatus.Committed, Diagnostic = "", WorkspaceUpdated = true, MergeDiagnostic = null });
        else if (Snapshot.Session?.State == MergeSessionState.Ready && Snapshot.Result?.Status == HistoryRestoreStatus.MutationFailedRecoveryRequired)
            Tracker.Update(s => s with { Result = null });
    }
}
