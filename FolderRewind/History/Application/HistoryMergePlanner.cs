using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryMergeSourceAction { Reuse, Remove, MergeFiles, SourceAddAdd, SourceDeleteModify, SourceModifyDelete, SourceBoundaryConflict }
public sealed record HistoryMergeSourcePlan(SourceId SourceId, CheckpointSource? Base, CheckpointSource? Ours,
    CheckpointSource? Theirs, HistoryMergeSourceAction Action, VersionId? ReuseVersionId);
public sealed record HistoryMergePlan(Guid Revision, HistoryMergeMode Mode, BranchUpdate Ours, BranchUpdate Theirs,
    CheckpointId? BaseCheckpointId, HistoryWorkspace ExpectedWorkspace, string ConfigRevision,
    ImmutableArray<HistoryRestoreSourceBinding> Bindings, ImmutableArray<HistoryMergeSourcePlan> Sources,
    string ProviderVersion = "generic-file/1", string PolicyVersion = "conservative/1");

public sealed class HistoryMergePlanner(HistoryRuntime history)
{
    public async Task<HistoryMergePlan> BuildAsync(BranchId sourceBranch, HistoryWorkspace workspace,
        string configRevision, IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token = default)
    {
        await history.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        if (workspace.ConfigId != history.ConfigId || workspace.ActiveBranchId is not { } targetBranch || targetBranch == sourceBranch)
            throw new InvalidOperationException("Merge requires distinct source and active target Branches in one Config.");
        var branches = HistoryBranchProjection.Build(await history.Query.GetAllBranchUpdatesAsync(token).ConfigureAwait(false));
        BranchUpdate Tip(BranchId id)
        {
            var branch = branches.SingleOrDefault(b => b.BranchId == id);
            if (branch is null || branch.Tips.Length != 1 || branch.Tips[0].IsDeleted || branch.Tips[0].IsUnborn)
                throw new InvalidOperationException("Merge inputs require unique, non-deleted, non-unborn local tips.");
            return branch.Tips[0];
        }
        var ours = Tip(targetBranch); var theirs = Tip(sourceBranch);
        if (workspace.ActiveBranchUpdateId != ours.UpdateId) throw new InvalidOperationException("Workspace target tip is stale.");
        var checkpoints = await history.Query.GetAllCheckpointsAsync(token).ConfigureAwait(false);
        var map = checkpoints.ToDictionary(c => c.CheckpointId);
        var found = new HistoryCheckpointGraph(checkpoints, history.ConfigId).FindBase(ours.TargetCheckpointId!.Value, theirs.TargetCheckpointId!.Value);
        var o = map[ours.TargetCheckpointId.Value]; var t = map[theirs.TargetCheckpointId.Value];
        foreach (var checkpoint in new[] { o, t })
        {
            var admission = await new HistoryExactCheckpointAdmission(history).EvaluateAsync(checkpoint, cancellationToken: token).ConfigureAwait(false);
            if (!admission.IsReady) throw new InvalidOperationException(admission.Diagnostic);
        }
        if (found.Mode is HistoryMergeMode.NoCommonBase or HistoryMergeMode.MultipleMergeBases or HistoryMergeMode.NoOp)
            return new(Guid.NewGuid(), found.Mode, ours, theirs, found.BaseCheckpointId, workspace, configRevision, bindings.ToImmutableArray(), []);
        var b = found.BaseCheckpointId is { } baseId ? map[baseId] : null;
        if (b is not null && !b.IsStructurallyComplete) throw new InvalidOperationException("Merge base roster is incomplete.");
        if (b is not null)
        {
            var admission = await new HistoryExactCheckpointAdmission(history).EvaluateAsync(b, cancellationToken: token).ConfigureAwait(false);
            if (!admission.IsReady) throw new InvalidOperationException(admission.Diagnostic);
        }
        var sources = new List<HistoryMergeSourcePlan>();
        foreach (var id in o.Sources.Concat(t.Sources).Concat(b?.Sources ?? []).Select(s => s.SourceId).Distinct().OrderBy(id => id.ToString(), StringComparer.Ordinal))
        {
            var bs = b?.Sources.SingleOrDefault(s => s.SourceId == id);
            var os = o.Sources.SingleOrDefault(s => s.SourceId == id); var ts = t.Sources.SingleOrDefault(s => s.SourceId == id);
            sources.Add(found.Mode == HistoryMergeMode.FastForwardLike
                ? new(id, null, os, ts, ts is null ? HistoryMergeSourceAction.Remove : HistoryMergeSourceAction.Reuse, ts?.VersionId)
                : PlanSource(id, bs, os, ts));
        }
        return new(Guid.NewGuid(), found.Mode, ours, theirs, found.BaseCheckpointId, workspace, configRevision,
            bindings.ToImmutableArray(), sources.ToImmutableArray());
    }

    internal static HistoryMergeSourcePlan PlanSource(SourceId id, CheckpointSource? b, CheckpointSource? o, CheckpointSource? t)
    {
        if (new[] { b, o, t }.Any(s => s is not null && s.VersionId is null)) throw new InvalidOperationException("Unknown Source is not deletion.");
        bool Same(CheckpointSource? x, CheckpointSource? y) => x?.VersionId == y?.VersionId
            && x?.EffectiveSourceBoundaryFingerprint == y?.EffectiveSourceBoundaryFingerprint;
        HistoryMergeSourcePlan Reuse(CheckpointSource? s) => new(id, b, o, t,
            s is null ? HistoryMergeSourceAction.Remove : HistoryMergeSourceAction.Reuse, s?.VersionId);
        if (Same(o, t)) return Reuse(o);
        if (Same(o, b)) return Reuse(t);
        if (Same(t, b)) return Reuse(o);
        var action = b is null ? HistoryMergeSourceAction.SourceAddAdd
            : o is null ? HistoryMergeSourceAction.SourceDeleteModify
            : t is null ? HistoryMergeSourceAction.SourceModifyDelete
            : b.EffectiveSourceBoundaryFingerprint != o.EffectiveSourceBoundaryFingerprint
                || b.EffectiveSourceBoundaryFingerprint != t.EffectiveSourceBoundaryFingerprint
                ? HistoryMergeSourceAction.SourceBoundaryConflict : HistoryMergeSourceAction.MergeFiles;
        return new(id, b, o, t, action, null);
    }
}
