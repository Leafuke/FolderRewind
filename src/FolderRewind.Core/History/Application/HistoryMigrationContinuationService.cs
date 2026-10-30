using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryContinuationCandidate(BranchId BranchId, BranchUpdateId UpdateId, string Name,
    CheckpointId CheckpointId, VersionId VersionId);
public sealed record HistoryContinuationSource(SourceId SourceId, bool HasHistory,
    ImmutableArray<HistoryContinuationCandidate> Candidates, BranchUpdateId? SuggestedUpdateId, bool RequiresReconciliation);
public sealed record HistoryContinuationPreview(HistoryConfigId ConfigId, long WorkspaceRevision,
    ImmutableArray<HistoryContinuationSource> Sources);
public sealed record HistoryContinuationSelection(SourceId SourceId, BranchUpdateId? UpdateId);

/// <summary>Continuation is an explicit ancestry decision; it does not assert that current files equal history.</summary>
public sealed class HistoryMigrationContinuationService(HistoryRuntime runtime,
    Func<HistoryRestoreSourceBinding, VersionId, CancellationToken, Task<bool>> matchesVersion)
{
    public async Task<HistoryContinuationPreview> PreviewAsync(IEnumerable<SourceId> sources,
        IEnumerable<WorkspaceSourceBaseline>? hints = null, CancellationToken token = default)
    {
        var workspace = await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false);
        if (workspace.Status != DeviceLocalStateStatus.Valid || workspace.Value is null)
            throw new DeviceLocalStateConflictException("Initialize or recover the Workspace before continuing a branch.");
        var branches = await runtime.Query.GetBranchesAsync(token).ConfigureAwait(false);
        var versions = await runtime.Query.GetAllVersionsAsync(token).ConfigureAwait(false);
        var hintMap = (hints ?? []).ToDictionary(h => h.SourceId);
        var result = ImmutableArray.CreateBuilder<HistoryContinuationSource>();
        foreach (var source in sources.Distinct())
        {
            var candidates = ImmutableArray.CreateBuilder<HistoryContinuationCandidate>();
            bool reconciliation = false;
            foreach (var branch in branches.Branches.Where(b => b.Tips.Any(t => t.SourceId == source)))
            {
                if (branch.Tips.Length != 1) { reconciliation = true; continue; }
                var tip = branch.Tips[0]; if (tip.IsDeleted) continue;
                if (tip.TargetCheckpointId is not { } checkpointId) continue;
                var checkpoint = await runtime.Query.GetCheckpointAsync(checkpointId, token).ConfigureAwait(false);
                if (checkpoint?.SourceId != source) continue;
                candidates.Add(new(tip.BranchId, tip.UpdateId, tip.Name, checkpoint.CheckpointId, checkpoint.VersionId));
            }
            var existing = workspace.Value.GetSourceState(source).ActiveBranchUpdateId;
            var suggested = candidates.Any(c => c.UpdateId == existing) ? existing
                : hintMap.TryGetValue(source, out var hint) && candidates.Any(c => c.UpdateId == hint.ActiveBranchUpdateId && c.BranchId == hint.ActiveBranchId)
                    ? hint.ActiveBranchUpdateId : candidates.Count == 1 && !reconciliation ? candidates[0].UpdateId : null;
            result.Add(new(source, versions.Any(v => v.SourceId == source), candidates.ToImmutable(), suggested, reconciliation));
        }
        return new(runtime.ConfigId, workspace.Value.StateRevision, result.ToImmutable());
    }

    public async Task<HistoryWorkspace> ApplyAsync(HistoryContinuationPreview preview,
        IReadOnlyList<HistoryRestoreSourceBinding> bindings, IReadOnlyList<HistoryContinuationSelection> selections,
        CancellationToken token = default)
    {
        if (preview.ConfigId != runtime.ConfigId || bindings.Select(b => b.SourceId).Distinct().Count() != bindings.Count
            || selections.Select(s => s.SourceId).Distinct().Count() != selections.Count
            || selections.Any(s => bindings.All(b => b.SourceId != s.SourceId)))
            throw new ArgumentException("Continuation selection does not match the current Source identities.");
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(runtime.ConfigId, token).ConfigureAwait(false);
        await using var mutation = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var workspace = (await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value
            ?? throw new DeviceLocalStateConflictException("Workspace is unavailable.");
        if (workspace.StateRevision != preview.WorkspaceRevision)
            throw new DeviceLocalStateConflictException("Workspace changed after continuation preview.");
        var changes = new List<WorkspaceSourceBaseline>();
        foreach (var selection in selections)
        {
            var source = preview.Sources.Single(s => s.SourceId == selection.SourceId);
            var binding = bindings.Single(b => b.SourceId == selection.SourceId);
            if (selection.UpdateId is not { } updateId)
            {
                if (source.HasHistory) throw new HistoryCommitConflictException("Select a historical branch or reconcile its tips before continuing.");
                changes.Add(new(selection.SourceId, null, WorkspaceBaselineRelation.Unknown, RequiresFullCapture: true));
                continue;
            }
            var candidate = source.Candidates.Single(c => c.UpdateId == updateId);
            var tips = await runtime.Query.GetBranchTipsAsync(candidate.BranchId, token).ConfigureAwait(false);
            if (tips.Count != 1 || tips[0].UpdateId != updateId || tips[0].IsDeleted || tips[0].SourceId != selection.SourceId)
                throw new HistoryCommitConflictException("Selected branch changed or requires reconciliation.");
            var checkpoint = await runtime.Query.GetCheckpointAsync(candidate.CheckpointId, token).ConfigureAwait(false);
            if (checkpoint?.SourceId != selection.SourceId || checkpoint.VersionId != candidate.VersionId)
                throw new HistoryCommitConflictException("Selected continuation checkpoint is invalid.");
            bool exact = await matchesVersion(binding, candidate.VersionId, token).ConfigureAwait(false);
            changes.Add(new(selection.SourceId, exact ? candidate.VersionId : null,
                exact ? WorkspaceBaselineRelation.Exact : WorkspaceBaselineRelation.Unknown,
                candidate.BranchId, candidate.UpdateId, candidate.CheckpointId, RequiresFullCapture: !exact));
        }
        var effective = changes.Where(s => workspace.GetSourceState(s.SourceId) != s).ToArray();
        if (effective.Length == 0) return workspace;
        var updated = workspace.WithSourceStates(effective);
        token.ThrowIfCancellationRequested();
        runtime.MigrationState.MarkImported(false);
        runtime.MigrationState.Prepare([HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(updated, workspace.StateRevision)]);
        await runtime.MigrationState.RecoverAsync(new(runtime.WorkspaceStore, runtime.LocalReplicaCatalogStore), CancellationToken.None).ConfigureAwait(false);
        await runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        runtime.ChangeFeed.Publish(runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
        return updated;
    }
}
