using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private MergeOperationService(BackupConfig config, SourceId source) { Config = config; SourceId = source; }
    public BackupConfig Config { get; }
    public SourceId SourceId { get; }
    public MergeOperationTracker Tracker { get; } = new();
    public MergeOperationSnapshot Snapshot => Tracker.Snapshot;
    public HistoryRuntime? Runtime { get; private set; }
    public IReadOnlyList<MergeSession> Sessions { get; private set; } = [];
    private HistoryRestoreService? _restore;
    private HistoryMergeService Core => new(Runtime!, _restore!);

    private Task Run(MergeOperationStage stage, Func<CancellationToken, Task> action, bool canStop = true)
    {
        if (_stopping) return Task.CompletedTask;
        return Tracker.RunAsync(stage, async token =>
        {
            var gate = Gates.GetOrAdd(Config.Id, _ => new(1, 1));
            if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
                throw new InvalidOperationException(I18n.GetString("MergeWorkspace_ConfigBusy"));
            try
            {
                Runtime ??= await NativeHistoryCoreGateway.EnsureReadyAsync(Config, token).ConfigureAwait(false);
                _restore ??= await NativeHistoryApplicationService.CreateRestoreServiceAsync(Config, token).ConfigureAwait(false);
                if (stage == MergeOperationStage.Saving) Tracker.Update(s => s with { IsSaved = false });
                await action(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.LogError($"Merge config={Config.Id} session={Snapshot.Session?.Id} stage={Snapshot.Stage}", "Merge", ex);
                throw;
            }
            finally { gate.Release(); }
        }, canStop);
    }

    private void SetSession(MergeSession? session) => Tracker.Update(s => s with { Session = session, IsSaved = true });
    private void Refresh()
    {
        // Result is already captured before this potentially failing read.
        Sessions = Runtime!.MergeSessions.List().Where(s => s.Plan.Ours.SourceId == SourceId).ToArray();
        if (Snapshot.Session is { } session) SetSession(Runtime.MergeSessions.Load(session.Id));
        Tracker.Update(s => s);
    }
    private MergeSession RequireSession() => Snapshot.Session ?? throw new InvalidOperationException(I18n.GetString("Merge_SelectBranch"));

    public Task LoadAsync(Guid? id = null) => Run(MergeOperationStage.Loading, async token =>
    {
        await _restore!.RecoverIncompleteAsync(token).ConfigureAwait(false);
        if (id is { } selected)
        {
            var session = Runtime!.MergeSessions.Load(selected);
            if (session.Plan.Ours.SourceId != SourceId) throw new InvalidOperationException("Merge source identity mismatch.");
            if (Snapshot.Session?.Id != id) Tracker.Update(s => s with { Result = null });
            SetSession(session);
        }
        Refresh();
    });

    public Task StartAsync(BranchId source) => Run(MergeOperationStage.Analyzing, async token =>
    {
        Tracker.Update(s => s with { Result = null, Session = null });
        SetSession(await NativeHistoryApplicationService.StartMergeAsync(Config, source, token).ConfigureAwait(false));
        if (Snapshot.Session is null) Tracker.Update(s => s with { Notice = "Merge_NoOp" });
        Refresh();
    });
    public Task RecomputeAsync() => Run(MergeOperationStage.Analyzing, async token =>
    {
        var before = Runtime!.MergeSessions.ConflictCounts(RequireSession()).Resolved;
        SetSession(await NativeHistoryApplicationService.RecomputeMergeAsync(Config, RequireSession(), token).ConfigureAwait(false));
        var retained = Runtime.MergeSessions.ConflictCounts(RequireSession()).Resolved;
        Tracker.Update(s => s with { Result = null });
        RecomputeSummary = string.Format(I18n.GetString("MergeWorkspace_Recomputed"), retained, Math.Max(0, before - retained));
        Refresh();
    });
    public Task PrepareReplicasAsync() => Run(MergeOperationStage.Downloading, async token =>
    {
        SetSession(await NativeHistoryApplicationService.PrepareMergeReplicasAsync(Config, RequireSession(), token).ConfigureAwait(false));
        Refresh();
    });
    public Task ResumeAsync() => Run(MergeOperationStage.Recovering, async token =>
    {
        await _restore!.RecoverIncompleteAsync(token).ConfigureAwait(false);
        var session = Runtime!.MergeSessions.Load(RequireSession().Id);
        if (session.State == MergeSessionState.Preparing) session = await Core.PrepareAsync(session, token).ConfigureAwait(false);
        SetSession(session); Refresh();
    }, false);
    public Task AbandonAsync() => Run(MergeOperationStage.Finishing, async token =>
    {
        await using var lease = await Runtime!.MutationGate.EnterAsync(token).ConfigureAwait(false);
        SetSession(Runtime.MergeSessions.Update(RequireSession(), MergeSessionState.Abandoned));
        var catalog = (await Runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (catalog is not null) Runtime.MergeSessions.CleanupTerminalArtifacts(catalog);
        Refresh();
    });
    public Task ResolveAsync(IReadOnlyList<MergeResolution> decisions) => Run(MergeOperationStage.Saving, _ =>
    {
        var before = ReadPrevious(RequireSession(), decisions.Select(d => d.ConflictId));
        var session = Runtime!.MergeSessions.ResolveBatch(RequireSession(), decisions);
        Remember(session, before); SetSession(session); Refresh(); return Task.CompletedTask;
    });
    public string? RecomputeSummary { get; private set; }
    public Task ImportAsync(MergeConflict conflict, string path) => Run(MergeOperationStage.Saving, async token =>
    {
        SetSession(await Core.ImportManualAsync(RequireSession(), conflict, path, token).ConfigureAwait(false)); Refresh();
    });
    public Task ApplyAsync() => Run(MergeOperationStage.Committing, async _ =>
    {
        var result = await NativeHistoryApplicationService.ApplyMergeAsync(Config, RequireSession(), CancellationToken.None).ConfigureAwait(false);
        Tracker.ReportResult(result); Refresh();
    }, false);
}
