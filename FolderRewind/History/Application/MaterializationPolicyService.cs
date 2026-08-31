using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record MaterializationPolicyProjectionResult(
    VersionId VersionId,
    MaterializationPolicyState EffectiveState,
    bool HasExplicitPolicy,
    ImmutableArray<MaterializationPolicyUpdate> Tips);

public static class MaterializationPolicyProjection
{
    public static MaterializationPolicyProjectionResult Project(
        VersionId versionId,
        IEnumerable<MaterializationPolicyUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var all = updates.Where(update => update.VersionId == versionId).ToImmutableArray();
        var parentIds = all.SelectMany(update => update.ParentUpdateIds).ToHashSet();
        var tips = all.Where(update => !parentIds.Contains(update.UpdateId))
            .OrderBy(update => update.CreatedAtUtc)
            .ThenBy(update => update.UpdateId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();
        var effective = tips.Length == 0 || tips.Any(update => update.State == MaterializationPolicyState.Retained)
            ? MaterializationPolicyState.Retained
            : MaterializationPolicyState.Released;
        return new(versionId, effective, all.Length > 0, tips);
    }
}

public sealed class MaterializationPolicyCommandException(string message) : Exception(message);

public sealed class MaterializationPolicyService
{
    private readonly HistoryRuntime _runtime;
    private readonly HistoryPackCodec _codec;

    public MaterializationPolicyService(HistoryRuntime runtime, HistoryPackCodec? codec = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _codec = codec ?? new HistoryPackCodec();
    }

    public async Task<MaterializationPolicyUpdate> SetAsync(
        VersionId versionId,
        MaterializationPolicyState state,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (await _runtime.Query.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new MaterializationPolicyCommandException($"Version {versionId} does not exist.");
        }
        if (state == MaterializationPolicyState.Released)
        {
            await EnsureCanReleaseAsync(versionId, cancellationToken).ConfigureAwait(false);
        }
        var currentTips = await _runtime.Query.GetMaterializationPolicyTipsAsync(versionId, cancellationToken).ConfigureAwait(false);
        var update = new MaterializationPolicyUpdate(
            MaterializationPolicyUpdateId.New(),
            versionId,
            currentTips.Select(tip => tip.UpdateId),
            state,
            DateTimeOffset.UtcNow,
            reason);
        await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime, _codec, [update], null, null, cancellationToken).ConfigureAwait(false);
        return update;
    }

    public async Task EnsureCanReleaseAsync(
        VersionId versionId,
        CancellationToken cancellationToken)
    {
        var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (workspaceLoad.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
        {
            throw new MaterializationPolicyCommandException("Cannot prove release safety while Workspace requires recovery.");
        }
        if (workspaceLoad.Value?.SourceBaselines.Any(baseline => baseline.BaseVersionId == versionId) == true)
        {
            throw new MaterializationPolicyCommandException("Version is protected by the current Workspace baseline.");
        }
        var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, versionId.Value);
        var annotation = await _runtime.Query.GetAnnotationProjectionAsync(target, cancellationToken).ConfigureAwait(false);
        if (annotation.IsPinned)
        {
            throw new MaterializationPolicyCommandException("Version is pinned and cannot be released.");
        }
        var checkpoints = await _runtime.Query.GetAllCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in checkpoints.Where(checkpoint =>
                     checkpoint.Sources.Any(source => source.VersionId == versionId)))
        {
            var checkpointTarget = new HistoryAnnotationTarget(
                HistoryAnnotationTargetKind.Checkpoint,
                checkpoint.CheckpointId.Value);
            if ((await _runtime.Query.GetAnnotationProjectionAsync(
                    checkpointTarget,
                    cancellationToken).ConfigureAwait(false)).IsPinned)
            {
                throw new MaterializationPolicyCommandException(
                    "Version is protected by a pinned Checkpoint and cannot be released.");
            }
        }
        var branches = await _runtime.Query.GetBranchesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var branch in branches.Branches.Where(branch => !branch.IsDeleted))
        {
            foreach (var tip in branch.Tips)
            {
                if (tip.TargetCheckpointId is not { } checkpointId) continue;
                var checkpoint = await _runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false);
                if (checkpoint?.Sources.Any(source => source.VersionId == versionId) == true)
                {
                    throw new MaterializationPolicyCommandException("Version is protected by a Branch tip.");
                }
            }
        }

        var activeSafetySnapshots = await _runtime.Query.GetSafetySnapshotProjectionsAsync(
            activeOnly: true,
            cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in activeSafetySnapshots)
        {
            var checkpoint = await _runtime.Query.GetCheckpointAsync(
                snapshot.Snapshot.CheckpointId,
                cancellationToken).ConfigureAwait(false);
            if (checkpoint?.Sources.Any(source => source.VersionId == versionId) == true)
            {
                throw new MaterializationPolicyCommandException(
                    "Version is protected by an active Safety Snapshot.");
            }
        }
    }

}
