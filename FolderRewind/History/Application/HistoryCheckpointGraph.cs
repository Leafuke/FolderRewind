using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.History.Application;

public enum HistoryMergeMode { NoOp, FastForwardLike, ThreeWay, NoCommonBase, MultipleMergeBases }
public sealed record HistoryMergeBase(HistoryMergeMode Mode, CheckpointId? BaseCheckpointId);

public sealed class HistoryCheckpointGraph
{
    private readonly Dictionary<CheckpointId, ConfigurationCheckpoint> _nodes;
    public HistoryCheckpointGraph(IEnumerable<ConfigurationCheckpoint> checkpoints, HistoryConfigId configId)
    {
        _nodes = checkpoints.ToDictionary(c => c.CheckpointId);
        var children = _nodes.Keys.ToDictionary(id => id, _ => new List<CheckpointId>());
        var remaining = new Dictionary<CheckpointId, int>();
        foreach (var node in _nodes.Values)
        {
            HistoryDomainValidator.ValidateNative(node);
            if (node.ConfigId != configId) throw new InvalidOperationException("Checkpoint graph crosses Config identity.");
            remaining[node.CheckpointId] = node.ParentCheckpointIds.Length;
            foreach (var parent in node.ParentCheckpointIds)
            {
                if (!children.TryGetValue(parent, out var list)) throw new InvalidOperationException("Checkpoint parent is missing.");
                list.Add(node.CheckpointId);
            }
        }
        var ready = new Queue<CheckpointId>(remaining.Where(p => p.Value == 0).Select(p => p.Key));
        int visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var child in children[id]) if (--remaining[child] == 0) ready.Enqueue(child);
        }
        if (visited != _nodes.Count) throw new InvalidOperationException("Checkpoint graph contains a cycle.");
    }

    public HistoryMergeBase FindBase(CheckpointId ours, CheckpointId theirs)
    {
        var o = Ancestors(ours); var t = Ancestors(theirs);
        if (o.Contains(theirs)) return new(HistoryMergeMode.NoOp, null);
        if (t.Contains(ours)) return new(HistoryMergeMode.FastForwardLike, null);
        o.IntersectWith(t);
        if (o.Count == 0) return new(HistoryMergeMode.NoCommonBase, null);
        var best = new HashSet<CheckpointId>(o);
        foreach (var candidate in o)
        {
            var ancestors = Ancestors(candidate); ancestors.Remove(candidate); best.ExceptWith(ancestors);
        }
        return best.Count == 1 ? new(HistoryMergeMode.ThreeWay, best.Single())
            : new(HistoryMergeMode.MultipleMergeBases, null);
    }

    private HashSet<CheckpointId> Ancestors(CheckpointId id)
    {
        var result = new HashSet<CheckpointId>(); var pending = new Stack<CheckpointId>(); pending.Push(id);
        while (pending.TryPop(out var current))
        {
            if (!result.Add(current)) continue;
            if (!_nodes.TryGetValue(current, out var node)) throw new InvalidOperationException("Checkpoint is missing.");
            foreach (var parent in node.ParentCheckpointIds) pending.Push(parent);
        }
        return result;
    }
}
