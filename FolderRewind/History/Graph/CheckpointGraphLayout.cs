using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FolderRewind.History.Graph;

/// <summary>
/// 泳道图的一个节点：一个节点就是一次<b>检查点</b>（一次备份一个点，合并点有两个父）。
/// <para>
/// 这里刻意只用自带类型（<see cref="CheckpointId"/> + 时间 + 父指针），<b>不引用</b>
/// <c>ConfigurationCheckpoint</c>：本算法要能被测试工程链接进去，而那个文件会拖进一整串未链接的域类型。
/// </para>
/// </summary>
public readonly record struct CheckpointGraphNode(
    CheckpointId Id,
    DateTimeOffset CreatedAtUtc,
    ImmutableArray<CheckpointId> ParentIds);

/// <summary>
/// 一行里要画的三组线。泳道号是<b>全局列号</b>，行与行之间靠它对齐。
/// </summary>
/// <param name="Index">行序，0 是最新的一行。</param>
/// <param name="Lane">本行节点的泳道，圆点画在这里。</param>
/// <param name="ColorIndex"><c>Lane % paletteSize</c> —— 颜色只用来区分线条，没有业务含义。</param>
/// <param name="IncomingLanes">上边界上汇聚进本节点的泳道（从 y=0 弯到圆心）；含本行泳道自身那条直入线。</param>
/// <param name="OutgoingLanes">本节点伸向各父节点的泳道（从圆心弯到下边界），按父的顺序排列，第一父就是本行泳道。</param>
/// <param name="PassThroughLanes">与本节点无关、只是路过的泳道（y=0 直通 y=H）。</param>
public sealed record CheckpointGraphRow(
    int Index,
    CheckpointId Id,
    int Lane,
    int ColorIndex,
    ImmutableArray<int> IncomingLanes,
    ImmutableArray<int> OutgoingLanes,
    ImmutableArray<int> PassThroughLanes);

/// <summary>
/// 整张图的布局结果。<see cref="LaneCount"/> 是<b>全局值</b> —— 所有行必须同宽，否则竖线在行与行之间对不齐。
/// <para>
/// 三个常量是「布局」与「渲染」之间的契约：算法不算像素，但列距与行高必须两边一致，
/// 所以放在这里当唯一出处（渲染端还有一条断言盯着 <see cref="PaletteSize"/>）。
/// </para>
/// </summary>
public sealed record CheckpointGraphLayout(int LaneCount, ImmutableArray<CheckpointGraphRow> Rows)
{
    /// <summary>一条泳道在界面上的宽度。列中心在 <c>lane * LaneWidth + LaneWidth / 2</c>。</summary>
    public const double LaneWidth = 22;

    /// <summary>一行的高度。<b>必须处处相同</b>：每行只画自己 0 到 RowHeight 的那一段，行长不一就接不上。</summary>
    public const double RowHeight = 80;

    /// <summary>
    /// 本应用用的泳道色数，也就是渲染端调色板的长度。<see cref="CheckpointGraphLayoutBuilder.Build"/>
    /// 的 <c>paletteSize</c> 参数是它的一般形式（测试会换成别的色数验证取模）。
    /// </summary>
    public const int PaletteSize = 8;
}

/// <summary>
/// 把「检查点 + 父指针」算成可以直接画的泳道图。
/// <para>
/// 顺序取<b>拓扑序（子先于父）</b>，不是单纯按时间倒序：同秒的多条、以及系统时钟被回拨时，
/// 时间序会把父排到子前面，连线就朝上接、泳道指向还没输出过的节点。
/// <b>时间是并列时的优先级，不是顺序本身。</b>
/// </para>
/// </summary>
public static class CheckpointGraphLayoutBuilder
{
    public static CheckpointGraphLayout Build(IReadOnlyList<CheckpointGraphNode> nodes, int paletteSize)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (paletteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(paletteSize), paletteSize, "调色板至少要有一个颜色。");
        }

        if (nodes.Count == 0)
        {
            return new CheckpointGraphLayout(0, []);
        }

        var order = TopologicalOrder(nodes, out var parents);

        // 每条泳道正在等哪个节点（null 表示这一列当前是空的）。列表只增不减，
        // 于是 LaneCount 就是「用过的最大列号 + 1」，被释放过的列也是稳定的宽度。
        var lanes = new List<CheckpointId?>();
        var rows = ImmutableArray.CreateBuilder<CheckpointGraphRow>(nodes.Count);

        for (var position = 0; position < order.Length; position++)
        {
            var node = nodes[order[position]];

            // 上边界的占用情况必须在本行释放任何泳道之前拍下来。
            var occupiedAbove = new bool[lanes.Count];
            for (var i = 0; i < lanes.Count; i++)
            {
                occupiedAbove[i] = lanes[i] is not null;
            }

            // 在等这个节点的泳道里，最左那条就是本行的泳道；其余几条都弯过来汇入。
            var lane = -1;
            var incoming = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] != node.Id)
                {
                    continue;
                }

                if (lane < 0)
                {
                    lane = i;
                }

                incoming.Add(i);
            }

            if (lane < 0)
            {
                lane = LowestFreeLane(lanes);
            }

            // 汇入的其余泳道立刻释放，好让下面的父节点接着用：
            // 合并点常见的「同一列上面是汇入、下面是分出去」就是这么排的。
            foreach (var i in incoming)
            {
                if (i != lane)
                {
                    lanes[i] = null;
                }
            }

            var outgoing = new List<int>();
            var inSetParents = parents[order[position]];
            for (var p = 0; p < inSetParents.Count; p++)
            {
                var parentId = nodes[inSetParents[p]].Id;
                var target = p == 0 ? lane : FindWaitingLane(lanes, parentId, lane);
                if (target < 0)
                {
                    target = LowestFreeLane(lanes);
                }

                lanes[target] = parentId;
                outgoing.Add(target);
            }

            if (inSetParents.Count == 0)
            {
                lanes[lane] = null; // 没有父（或父都在集合外）：这一列到此为止
            }

            var passThrough = new List<int>();
            for (var i = 0; i < occupiedAbove.Length; i++)
            {
                // 上方有来线、本行不动它的列，就是贯穿列。它此刻必然仍被占用：
                // 只有 incoming 里的列会在本行被释放，而那批（除本行泳道外）已被排掉。
                if (!occupiedAbove[i] || i == lane)
                {
                    continue;
                }

                if (incoming.Contains(i) || outgoing.Contains(i))
                {
                    continue;
                }

                passThrough.Add(i);
            }

            rows.Add(new CheckpointGraphRow(position, node.Id, lane, lane % paletteSize,
                [.. incoming], [.. outgoing], [.. passThrough]));
        }

        return new CheckpointGraphLayout(lanes.Count, rows.ToImmutable());
    }

    /// <summary>
    /// 子先于父的排列。同批候选里先出新近的（<b>时间是并列时的优先级</b>），同一时刻再按 Id 序数排，保证结果稳定。
    /// <para>
    /// 重复 Id 与父指针成环都直接抛：这两种都不该发生，静默兜底只会把错的布局画到界面上。
    /// 父指针指向集合外的节点是正常情形（父已经不在当前配置的历史里），当终点看待，不抛。
    /// </para>
    /// </summary>
    private static int[] TopologicalOrder(IReadOnlyList<CheckpointGraphNode> nodes, out List<int>[] parents)
    {
        var index = new Dictionary<CheckpointId, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!index.TryAdd(nodes[i].Id, i))
            {
                throw new ArgumentException($"检查点 {nodes[i].Id} 在图中出现了两次。", nameof(nodes));
            }
        }

        parents = new List<int>[nodes.Count];
        var pendingChildren = new int[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            var list = new List<int>();
            var seen = new HashSet<int>();
            var declared = nodes[i].ParentIds;
            if (!declared.IsDefault)
            {
                foreach (var parentId in declared)
                {
                    if (index.TryGetValue(parentId, out var parent) && seen.Add(parent))
                    {
                        list.Add(parent);
                        pendingChildren[parent]++;
                    }
                }
            }

            parents[i] = list;
        }

        var ready = new PriorityQueue<int, (long NewestFirst, string Id)>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (pendingChildren[i] == 0)
            {
                ready.Enqueue(i, Priority(nodes[i]));
            }
        }

        var order = new int[nodes.Count];
        var emitted = 0;
        while (ready.TryDequeue(out var current, out _))
        {
            order[emitted++] = current;
            foreach (var parent in parents[current])
            {
                if (--pendingChildren[parent] == 0)
                {
                    ready.Enqueue(parent, Priority(nodes[parent]));
                }
            }
        }

        if (emitted != nodes.Count)
        {
            throw new InvalidOperationException("检查点的父指针成环，泳道图排不出来。");
        }

        return order;
    }

    // 取负是为了让「更新的」排在前面：PriorityQueue 每次出的是最小优先级。
    private static (long NewestFirst, string Id) Priority(CheckpointGraphNode node)
        => (-node.CreatedAtUtc.UtcDateTime.Ticks, node.Id.ToString());

    private static int LowestFreeLane(List<CheckpointId?> lanes)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] is null)
            {
                return i;
            }
        }

        lanes.Add(null);
        return lanes.Count - 1;
    }

    /// <summary>已经在等这个父节点的泳道（最左一条）。找到就复用它，别新开一列 —— 那正是「两条线合并成一列」。</summary>
    private static int FindWaitingLane(List<CheckpointId?> lanes, CheckpointId parentId, int exclude)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            if (i != exclude && lanes[i] == parentId)
            {
                return i;
            }
        }

        return -1;
    }
}
