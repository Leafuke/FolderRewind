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
        var parentIds = all.SelectMany(update => update.ParentUpdateIds).ToHashSet();
        var branchTips = all
            .Where(update => !parentIds.Contains(update.UpdateId))
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
}
