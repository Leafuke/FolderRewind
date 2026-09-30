using FolderRewind.History.Domain;
using FolderRewind.History.Graph;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

/// <summary>
/// 泳道布局的算法测试。这里守的都是「画出来会歪」的那几件事：
/// 顺序必须是子先于父（时间是并列时的优先级，不是顺序本身）、列要复用、
/// 非法输入要当场抛而不是画一张错的图。
/// </summary>
[TestClass]
public sealed class CheckpointGraphLayoutTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void EmptyGraphHasNoLanesAndNoRows()
    {
        var layout = CheckpointGraphLayoutBuilder.Build([], 8);

        Assert.AreEqual(0, layout.LaneCount);
        Assert.IsEmpty(layout.Rows);
    }

    [TestMethod]
    public void SingleNodeOccupiesTheFirstLaneWithNothingToDraw()
    {
        var layout = Build(Node(1, 1));

        var row = Single(layout);
        Assert.AreEqual(Id(1), row.Id);
        Assert.AreEqual(0, row.Lane);
        Assert.AreEqual(0, row.ColorIndex);
        AssertLanes([], row.IncomingLanes);
        AssertLanes([], row.OutgoingLanes);
        AssertLanes([], row.PassThroughLanes);
    }

    [TestMethod]
    public void LinearChainStaysInOneLane()
    {
        // 1 的父是 2，2 的父是 3：一条直线，三行都在第 0 列。
        var layout = Build(Node(1, 3, 2), Node(2, 2, 3), Node(3, 1));

        Assert.AreEqual(1, layout.LaneCount);
        Assert.AreEqual(0, layout.Rows[0].Lane);
        Assert.AreEqual(0, layout.Rows[1].Lane);
        Assert.AreEqual(0, layout.Rows[2].Lane);
        AssertLanes([], layout.Rows[0].IncomingLanes);
        AssertLanes([0], layout.Rows[1].IncomingLanes);
        AssertLanes([0], layout.Rows[2].IncomingLanes);
        AssertLanes([0], layout.Rows[0].OutgoingLanes);
        AssertLanes([0], layout.Rows[1].OutgoingLanes);
        AssertLanes([], layout.Rows[2].OutgoingLanes);
        AssertLanes([], layout.Rows[0].PassThroughLanes);
    }

    [TestMethod]
    public void TwoChildrenConvergeOnTheirParent()
    {
        // 两个子各占一列，最后一起汇进父的那一列 —— 这就是「分叉后又并回来」。
        var layout = Build(Node(1, 5, 3), Node(2, 4, 3), Node(3, 1));

        Assert.AreEqual(2, layout.LaneCount);
        Assert.AreEqual(0, layout.Rows[0].Lane);
        Assert.AreEqual(1, layout.Rows[1].Lane);
        Assert.AreEqual(0, layout.Rows[2].Lane);
        AssertLanes([0], layout.Rows[0].OutgoingLanes);
        AssertLanes([1], layout.Rows[1].OutgoingLanes);
        AssertLanes([0, 1], layout.Rows[2].IncomingLanes);
        Assert.AreEqual(0, layout.Rows[2].ColorIndex);
    }

    [TestMethod]
    public void ParentStaysInTheNodeLaneSoTheFirstParentIsStraightDown()
    {
        var layout = Build(Node(1, 3, 2), Node(2, 2, 3), Node(3, 1));

        // 第一父永远接着本行的泳道往下，这样主线才是直上直下的一条。
        Assert.AreEqual(layout.Rows[0].Lane, layout.Rows[0].OutgoingLanes[0]);
        Assert.AreEqual(layout.Rows[1].Lane, layout.Rows[1].OutgoingLanes[0]);
    }

    [TestMethod]
    public void MergeNodeOpensASecondLaneForItsOtherParent()
    {
        // 合并点：首父续本列，第二父另开一列。
        var layout = Build(Node(1, 5, 2, 3), Node(2, 4), Node(3, 3));

        Assert.AreEqual(2, layout.LaneCount);
        AssertLanes([0, 1], layout.Rows[0].OutgoingLanes);

        // 首父那行：第二父的线从上面穿到下面，与本行无关。
        Assert.AreEqual(0, layout.Rows[1].Lane);
        AssertLanes([0], layout.Rows[1].IncomingLanes);
        AssertLanes([1], layout.Rows[1].PassThroughLanes);

        // 第二父自己落在第 1 列。
        Assert.AreEqual(1, layout.Rows[2].Lane);
        AssertLanes([1], layout.Rows[2].IncomingLanes);
    }

    [TestMethod]
    public void ALaneFreedByATerminalNodeIsReused()
    {
        // 两个互不相干的端点：第一个用完就把第 0 列还回去了，第二个接着用同一列。
        var layout = Build(Node(1, 2), Node(2, 1));

        Assert.AreEqual(1, layout.LaneCount);
        Assert.AreEqual(0, layout.Rows[0].Lane);
        Assert.AreEqual(0, layout.Rows[1].Lane);
        AssertLanes([], layout.Rows[0].OutgoingLanes);
    }

    [TestMethod]
    public void ParentsOutsideTheGraphAreTreatedAsTerminal()
    {
        // 父已经不在当前配置的历史里是正常情形：当终点看，不抛，也不留一根断线。
        var layout = Build(Node(1, 2, 99));

        var row = Single(layout);
        AssertLanes([], row.OutgoingLanes);
        Assert.AreEqual(1, layout.LaneCount);
    }

    [TestMethod]
    public void ParentIsDrawnAfterItsChild()
    {
        // 1 的父是 2；2 与 3 的父是 4。
        var layout = Build(Node(1, 5, 2), Node(2, 4, 4), Node(3, 3, 4), Node(4, 1));

        var index = layout.Rows.Select((row, position) => (row.Id, position))
            .ToDictionary(item => item.Id, item => item.position);
        Assert.IsGreaterThan(index[Id(1)], index[Id(2)], "子的父必须排在子后面");
        Assert.IsGreaterThan(index[Id(2)], index[Id(4)]);
        Assert.IsGreaterThan(index[Id(3)], index[Id(4)]);
    }

    [TestMethod]
    public void ClockSkewDoesNotPullAParentAboveItsChild()
    {
        // 父的时间戳比子还新（系统时钟被回拨过）。按时间倒序排会把父排到前面，
        // 连线就会朝上接 —— 所以顺序得由拓扑定，时间只是在并列时分先后。
        var layout = Build(Node(1, 5, 2), Node(2, 999));

        Assert.AreEqual(Id(1), layout.Rows[0].Id);
        Assert.AreEqual(Id(2), layout.Rows[1].Id);
    }

    [TestMethod]
    public void SameTimestampStillKeepsTheChildAboveTheParent()
    {
        var layout = Build(Node(1, 5, 2), Node(2, 5));

        Assert.AreEqual(Id(1), layout.Rows[0].Id);
        Assert.AreEqual(Id(2), layout.Rows[1].Id);
    }

    [TestMethod]
    public void ColorIndexIsTheLaneModuloThePaletteSize()
    {
        // 两列的那张图：第 1 行落在第 1 列。
        var nodes = new[] { Node(1, 5, 3), Node(2, 4, 3), Node(3, 1) };

        var monochrome = CheckpointGraphLayoutBuilder.Build(nodes, 1);
        var twoColours = CheckpointGraphLayoutBuilder.Build(nodes, 2);

        Assert.AreEqual(1, twoColours.Rows[1].Lane);
        Assert.AreEqual(0, monochrome.Rows[1].ColorIndex);
        Assert.AreEqual(1, twoColours.Rows[1].ColorIndex);
    }

    [TestMethod]
    public void RowsAreInternallyConsistent()
    {
        var layout = Build(Node(1, 6, 2, 3), Node(2, 5, 4), Node(3, 4, 4), Node(4, 3));

        for (var position = 0; position < layout.Rows.Length; position++)
        {
            var row = layout.Rows[position];
            Assert.AreEqual(position, row.Index);
            Assert.AreEqual(row.Lane % CheckpointGraphLayout.PaletteSize, row.ColorIndex);
            Assert.IsLessThan(layout.LaneCount, row.Lane);

            // 同一条泳道在一行里最多被用一次 —— 只有本行泳道例外：
            // 它在圆点上方是直入线、下方是直出线，本来就是同一列的上下两半。
            foreach (var lanes in new[] { row.IncomingLanes, row.OutgoingLanes, row.PassThroughLanes })
            {
                Assert.AreEqual(lanes.Length, lanes.Distinct().Count(), $"第 {position} 行的泳道重复占用了");
            }

            foreach (var lane in row.PassThroughLanes)
            {
                Assert.AreNotEqual(row.Lane, lane, "贯穿线不该画在本行的节点泳道上");
                Assert.DoesNotContain(lane, row.IncomingLanes, "贯穿线与汇入线不能占用同一条泳道");
                Assert.DoesNotContain(lane, row.OutgoingLanes, "贯穿线与伸出线不能占用同一条泳道");
            }

            foreach (var lane in row.IncomingLanes.Intersect(row.OutgoingLanes))
            {
                Assert.AreEqual(row.Lane, lane, "汇入与伸出只有本行泳道那条直线可以共用");
            }
        }
    }

    [TestMethod]
    public void DuplicateCheckpointIdThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Build(Node(1, 2), Node(1, 1)));
    }

    [TestMethod]
    public void ParentCycleThrows()
    {
        // 1 的父是 2，2 的父又是 1：排不出顺序，也不要静默画一张错的图。
        Assert.ThrowsExactly<InvalidOperationException>(() => Build(Node(1, 2, 2), Node(2, 1, 1)));
    }

    [TestMethod]
    public void NonPositivePaletteSizeThrows()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckpointGraphLayoutBuilder.Build([Node(1, 1)], 0));
    }

    private static CheckpointGraphLayout Build(params CheckpointGraphNode[] nodes)
        => CheckpointGraphLayoutBuilder.Build(nodes, CheckpointGraphLayout.PaletteSize);

    private static CheckpointGraphRow Single(CheckpointGraphLayout layout)
    {
        Assert.HasCount(1, layout.Rows);
        return layout.Rows[0];
    }

    private static CheckpointGraphNode Node(int id, int day, params int[] parents)
        => new(Id(id), Origin.AddDays(day), [.. parents.Select(Id)]);

    private static CheckpointId Id(int seed)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, seed);
        return CheckpointId.FromGuid(new Guid(bytes));
    }

    private static void AssertLanes(int[] expected, ImmutableArray<int> actual)
        => Assert.IsTrue(expected.SequenceEqual(actual),
            $"泳道应为 [{string.Join(",", expected)}]，实际 [{string.Join(",", actual)}]。");
}
