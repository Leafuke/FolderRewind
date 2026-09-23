using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using System.Diagnostics;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeScaleTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly HistoryConfigId Config = new("merge-scale");
    private static ConfigurationCheckpoint Node(params CheckpointId[] parents) => new(CheckpointId.New(), Config,
        DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"), [], parents,
        parents.Length == 2 ? CheckpointCreationKind.Merge : CheckpointCreationKind.Capture);

    [TestMethod]
    [DataRow(10000)]
    [DataRow(50000)]
    public void LongCommonChainAndCrissCrossUseLinearVisits(int count)
    {
        var allocation = GC.GetTotalAllocatedBytes(); var timer = Stopwatch.StartNew();
        var nodes = new List<ConfigurationCheckpoint> { Node() };
        for (int i = 1; i < count; i++) nodes.Add(Node(nodes[^1].CheckpointId));
        var a = Node(nodes[^1].CheckpointId); var b = Node(nodes[^1].CheckpointId);
        var x = Node(a.CheckpointId, b.CheckpointId); var y = Node(b.CheckpointId, a.CheckpointId);
        nodes.AddRange([a, b, x, y]); var graph = new HistoryCheckpointGraph(nodes, Config);
        Assert.AreEqual(new HistoryMergeBase(HistoryMergeMode.ThreeWay, nodes[count - 1].CheckpointId), graph.FindBase(a.CheckpointId, b.CheckpointId));
        Assert.IsLessThanOrEqualTo(3L * (nodes.Count + nodes.Sum(n => n.ParentCheckpointIds.Length)), graph.LastTraversalCount);
        Assert.AreEqual(HistoryMergeMode.MultipleMergeBases, graph.FindBase(x.CheckpointId, y.CheckpointId).Mode);
        Assert.IsLessThanOrEqualTo(3L * (nodes.Count + nodes.Sum(n => n.ParentCheckpointIds.Length)), graph.LastTraversalCount);
        TestContext.WriteLine($"nodes={count}, visits={graph.LastTraversalCount}, elapsedMs={timer.ElapsedMilliseconds}, allocated={GC.GetTotalAllocatedBytes() - allocation}");
    }

    [TestMethod]
    public void SmallGraphsMatchIndependentNaiveOracle()
    {
        var random = new Random(901);
        for (int trial = 0; trial < 40; trial++)
        {
            var nodes = new List<ConfigurationCheckpoint> { Node() };
            for (int i = 1; i < 35; i++) nodes.Add(Node(Enumerable.Range(0, random.Next(0, 3))
                .Select(_ => nodes[random.Next(nodes.Count)].CheckpointId).Distinct().ToArray()));
            var byId = nodes.ToDictionary(n => n.CheckpointId); var graph = new HistoryCheckpointGraph(nodes, Config);
            HashSet<CheckpointId> Ancestors(CheckpointId id)
            {
                var result = new HashSet<CheckpointId> { id };
                foreach (var parent in byId[id].ParentCheckpointIds) result.UnionWith(Ancestors(parent));
                return result;
            }
            foreach (var left in nodes.TakeLast(8)) foreach (var right in nodes.TakeLast(8))
            {
                var a = Ancestors(left.CheckpointId); var b = Ancestors(right.CheckpointId);
                HistoryMergeBase expected;
                if (a.Contains(right.CheckpointId)) expected = new(HistoryMergeMode.NoOp, null);
                else if (b.Contains(left.CheckpointId)) expected = new(HistoryMergeMode.FastForwardLike, null);
                else
                {
                    var common = a.Intersect(b).ToArray();
                    var best = common.Where(c => !common.Any(other => other != c && Ancestors(other).Contains(c))).ToArray();
                    expected = new(best.Length == 0 ? HistoryMergeMode.NoCommonBase : best.Length == 1 ? HistoryMergeMode.ThreeWay : HistoryMergeMode.MultipleMergeBases,
                        best.Length == 1 ? best[0] : null);
                }
                Assert.AreEqual(expected, graph.FindBase(left.CheckpointId, right.CheckpointId));
            }
        }
    }

    [TestMethod]
    public void HundredThousandConflictsPageBySourceAndResolveInOneTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "merge-scale-" + Guid.NewGuid().ToString("N"));
        var allocation = GC.GetTotalAllocatedBytes(); var timer = Stopwatch.StartNew();
        try
        {
            var cp = Node();
            var a = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "a", cp.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
            var b = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "b", cp.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
            var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, a, b, cp.CheckpointId,
                new HistoryWorkspace(Config, 0, a.BranchId, a.UpdateId, []), "config", [], []);
            var store = new MergeSessionStore(root); var session = store.Create(plan, []);
            var sources = Enumerable.Range(0, 4).Select(_ => SourceId.New()).ToArray();
            foreach (var source in sources)
                store.SaveSource(session, new(new(source, null, null, null, HistoryMergeSourceAction.MergeFiles, null),
                    MergeTreeManifest.Empty, MergeTreeManifest.Empty, MergeTreeManifest.Empty, MergeTreeManifest.Empty),
                    Enumerable.Range(0, 25000).Select(i => new MergeConflict($"{source}-{i:D5}", new(source, [$"file-{i}"], null),
                        MergeConflictKind.ModifyModify, $"{source}-{i}", MergeTreeManifest.Empty.Files, MergeTreeManifest.Empty.Files, MergeTreeManifest.Empty.Files)));
            session = store.CompletePreparation(session, []);
            IEnumerable<MergeResolution> Resolutions()
            {
                foreach (var source in sources)
                {
                    var after = ""; var seen = 0;
                    while (true)
                    {
                        var page = store.ConflictPage(session, after, source);
                        foreach (var item in page)
                        {
                            Assert.AreEqual(source, item.Conflict.Subject.SourceId); seen++;
                            yield return new(session.Plan.Revision, item.Conflict.Id, item.Conflict.InputSignature, MergeResolutionChoice.Ours);
                        }
                        if (page.Count < 500) break;
                        after = page[^1].Conflict.Id;
                    }
                    Assert.AreEqual(25000, seen);
                }
            }
            session = store.ResolveBatch(session, Resolutions());
            Assert.AreEqual(MergeSessionState.Ready, session.State);
            Assert.AreEqual(1, store.ResolutionTransactionCount);
            Assert.AreEqual(500, store.MaxConflictPageSize);
            Assert.AreEqual(204, store.ConflictReadCount);
            TestContext.WriteLine($"conflicts=100000, reads={store.ConflictReadCount}, batchTransactions={store.ResolutionTransactionCount}, elapsedMs={timer.ElapsedMilliseconds}, allocated={GC.GetTotalAllocatedBytes() - allocation}, peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64}");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
