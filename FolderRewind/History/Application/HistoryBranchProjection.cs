using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FolderRewind.History.Application;

public sealed record HistoryBranchState(
    BranchId BranchId,
    ImmutableArray<BranchUpdate> Tips,
    bool IsMultiTip,
    bool IsDeleted,
    bool HasNameCollision);

public sealed record HistoryBranchNameCollision(
    string Name,
    ImmutableArray<BranchId> BranchIds);

public sealed record HistoryBranchQueryResult(
    ImmutableArray<HistoryBranchState> Branches,
    ImmutableArray<HistoryBranchNameCollision> BranchNameCollisions);

public static class HistoryBranchProjection
{
    public static ImmutableArray<HistoryBranchState> Build(IEnumerable<BranchUpdate> updates)
        => Query(updates).Branches;

    public static HistoryBranchQueryResult Query(IEnumerable<BranchUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var all = updates.ToImmutableArray();
        var branchTips = FindLocalTips(all)
            .GroupBy(update => update.BranchId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(update => update.CreatedAtUtc)
                    .ThenBy(update => update.UpdateId.ToString(), StringComparer.Ordinal)
                    .ToImmutableArray());

        var collisionGroups = branchTips
            .SelectMany(pair => pair.Value
                .Where(tip => !tip.IsDeleted)
                .Select(tip => (Name: tip.Name, pair.Key)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HistoryBranchNameCollision(
                group.OrderBy(item => item.Name, StringComparer.Ordinal).First().Name,
                group.Select(item => item.Key)
                    .Distinct()
                    .OrderBy(id => id.ToString(), StringComparer.Ordinal)
                    .ToImmutableArray()))
            .Where(collision => collision.BranchIds.Length > 1)
            .OrderBy(collision => collision.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var collidingIds = collisionGroups.SelectMany(collision => collision.BranchIds).ToHashSet();

        var branches = branchTips
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => new HistoryBranchState(
                pair.Key,
                pair.Value,
                pair.Value.Length > 1,
                pair.Value.Length == 1 && pair.Value[0].IsDeleted,
                collidingIds.Contains(pair.Key)))
            .ToImmutableArray();
        return new HistoryBranchQueryResult(branches, collisionGroups);
    }

    public static ImmutableArray<BranchUpdate> FindLocalTips(IEnumerable<BranchUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var all = updates.ToImmutableArray();
        var consumed = all
            .SelectMany(child => child.ParentUpdateIds.Select(parentId => (child.BranchId, parentId)))
            .ToHashSet();
        // Branch ownership 与全局祖先关系不同：只有同 Branch child 才能消费 local tip。
        return all
            .Where(update => !consumed.Contains((update.BranchId, update.UpdateId)))
            .ToImmutableArray();
    }

    public static ImmutableArray<BranchUpdate> LocalLineage(
        BranchUpdate tip,
        IEnumerable<BranchUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(tip);
        var map = updates.ToDictionary(item => item.UpdateId);
        var result = new List<BranchUpdate>();
        var pending = new Stack<BranchUpdateId>();
        pending.Push(tip.UpdateId);
        var visited = new HashSet<BranchUpdateId>();
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id) || !map.TryGetValue(id, out var update) || update.BranchId != tip.BranchId)
                continue;
            result.Add(update);
            foreach (var parentId in update.ParentUpdateIds)
                pending.Push(parentId);
        }
        return result.ToImmutableArray();
    }

    public static bool IsGlobalAncestor(
        BranchUpdateId ancestorId,
        BranchUpdateId descendantId,
        IEnumerable<BranchUpdate> updates)
    {
        var map = updates.ToDictionary(item => item.UpdateId);
        var pending = new Stack<BranchUpdateId>();
        pending.Push(descendantId);
        var visited = new HashSet<BranchUpdateId>();
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id)) continue;
            if (id == ancestorId) return true;
            if (map.TryGetValue(id, out var update))
                foreach (var parentId in update.ParentUpdateIds) pending.Push(parentId);
        }
        return false;
    }
}

public sealed record HistoryBranchMembershipProjectionResult(
    ImmutableDictionary<CheckpointId, ImmutableArray<BranchId>> CheckpointBranches,
    ImmutableDictionary<VersionId, ImmutableArray<BranchId>> VersionBranches);

/// <summary>
/// Projects BranchUpdate ancestry onto Checkpoints and SourceVersions. A branch point is
/// intentionally shared: if two branches target the same Checkpoint, every Version in that
/// Checkpoint belongs to both branches until their later Checkpoints diverge.
/// </summary>
public static class HistoryBranchMembershipProjection
{
    public static HistoryBranchMembershipProjectionResult Build(
        IEnumerable<BranchUpdate> updates,
        IEnumerable<ConfigurationCheckpoint> checkpoints)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(checkpoints);
        var allUpdates = updates.ToImmutableArray();
        var allCheckpoints = checkpoints.ToImmutableArray();
        var updatesById = allUpdates.ToDictionary(update => update.UpdateId);
        var checkpointBranches = new Dictionary<CheckpointId, HashSet<BranchId>>();

        foreach (var branch in HistoryBranchProjection.Build(allUpdates))
        {
            var pending = new Stack<BranchUpdateId>(branch.Tips.Select(tip => tip.UpdateId));
            var visited = new HashSet<BranchUpdateId>();
            while (pending.TryPop(out var updateId))
            {
                if (!visited.Add(updateId)
                    || !updatesById.TryGetValue(updateId, out var update)
                    || update.BranchId != branch.BranchId)
                {
                    continue;
                }

                if (update.TargetCheckpointId is { } checkpointId)
                {
                    if (!checkpointBranches.TryGetValue(checkpointId, out var branchIds))
                    {
                        branchIds = [];
                        checkpointBranches[checkpointId] = branchIds;
                    }
                    branchIds.Add(branch.BranchId);
                }
                foreach (var parentId in update.ParentUpdateIds) pending.Push(parentId);
            }
        }

        var versionBranches = new Dictionary<VersionId, HashSet<BranchId>>();
        foreach (var checkpoint in allCheckpoints)
        {
            if (!checkpointBranches.TryGetValue(checkpoint.CheckpointId, out var branchIds)) continue;
            foreach (var source in checkpoint.Sources.Where(source => source.VersionId is not null))
            {
                var versionId = source.VersionId!.Value;
                if (!versionBranches.TryGetValue(versionId, out var memberships))
                {
                    memberships = [];
                    versionBranches[versionId] = memberships;
                }
                memberships.UnionWith(branchIds);
            }
        }

        return new HistoryBranchMembershipProjectionResult(
            checkpointBranches.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToImmutableArray()),
            versionBranches.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToImmutableArray()));
    }
}
