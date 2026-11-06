using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using System.Collections;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryGraphTraversalTests
{
    private static readonly HistoryConfigId Config = new("graph-traversal-tests");
    private static readonly SourceId Source = SourceId.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SmallFailureGraphsMatchRecursiveOracleAcrossRootOrdersAndMemoReuse()
    {
        // The bounded recursive oracle is intentionally independent of the explicit
        // traversal. Cycles, missing references and alternative roots are legal test inputs.
        for (var seed = 0; seed < 48; seed++)
        {
            var random = new Random(seed);
            var ids = Enumerable.Range(0, 8).Select(_ => VersionId.New()).ToArray();
            var repIds = Enumerable.Range(0, 12).Select(_ => RepresentationId.New()).ToArray();
            var missingVersion = VersionId.New();
            var missingRep = RepresentationId.New();
            var versions = ids.Select((id, index) => Version(id,
                Enumerable.Range(0, random.Next(3)).Select(_ => random.Next(6) == 0 ? missingVersion : ids[random.Next(ids.Length)]).Distinct(),
                random.Next(3) != 0,
                random.Next(7) == 0 ? SourceId.New() : Source,
                random.Next(6) == 0 ? new HistoryConfigId("other") : Config,
                random.Next(5) == 0 ? new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.Include, ["data/**"],
                    EffectiveBoundaryFilterMode.Blacklist, [], false) : null)).ToDictionary(v => v.VersionId);
            var reps = repIds.Select((id, index) => Representation(id, ids[index % ids.Length],
                Enumerable.Range(0, random.Next(3)).Select(_ => random.Next(6) == 0 ? missingRep : repIds[random.Next(repIds.Length)]).Distinct(),
                random.Next(5) == 0 ? MaterializationFidelity.Partial : MaterializationFidelity.Exact,
                random.Next(6) == 0 ? null : random.Next(20))).ToDictionary(r => r.RepresentationId);
            var local = repIds.Where(_ => random.Next(3) != 0).ToHashSet();
            var remote = repIds.Where(_ => random.Next(2) != 0).ToHashSet();
            bool Present(VersionRepresentation node, bool allowRemote)
                => local.Contains(node.RepresentationId) || allowRemote && remote.Contains(node.RepresentationId);
            foreach (var reverse in new[] { false, true })
            {
                var admission = new HistoryExactCheckpointAdmission.EvaluationContext(versions, reps);
                var availability = new HistoryPresentationQueryService.AvailabilityContext(reps, Present);
                var orderedVersions = reverse ? versions.Values.Reverse() : versions.Values.AsEnumerable();
                var orderedReps = reverse ? reps.Values.Reverse() : reps.Values.AsEnumerable();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    foreach (var version in orderedVersions)
                    {
                        var expected = OracleAdmission(version, versions, reps);
                        Assert.AreEqual(expected, admission.Evaluate(Checkpoint(version)).Status, $"seed={seed}, reverse={reverse}, repeat={repeat}");
                    }
                    foreach (var node in orderedReps)
                    {
                        foreach (var allowRemote in new[] { true, false })
                            Assert.AreEqual(OracleAvailable(node, reps, Present, allowRemote, []),
                                availability.IsAvailable(node, allowRemote), $"availability seed={seed}, remote={allowRemote}");
                        Assert.AreEqual(OracleBytes(node, reps), BackupSizeMetadata.DependencyBytes(node, reps), $"bytes seed={seed}");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void DiamondAndMultipleParentsKeepAndOrAndPerRootSizeSemantics()
    {
        var full = Version(VersionId.New(), [], false);
        var left = Version(VersionId.New(), [full.VersionId], true);
        var right = Version(VersionId.New(), [full.VersionId], true);
        var tip = Version(VersionId.New(), [left.VersionId, right.VersionId], true);
        var versions = new[] { full, left, right, tip }.ToDictionary(v => v.VersionId);
        var shared = Representation(RepresentationId.New(), full.VersionId, [], bytes: 100);
        var a = Representation(RepresentationId.New(), left.VersionId, [shared.RepresentationId], bytes: 10);
        var b = Representation(RepresentationId.New(), right.VersionId, [shared.RepresentationId], bytes: 20);
        var root = Representation(RepresentationId.New(), tip.VersionId, [a.RepresentationId, b.RepresentationId], bytes: 1);
        var cycleId = RepresentationId.New();
        var cycle = Representation(cycleId, tip.VersionId, [cycleId]);
        var reps = new[] { cycle, shared, a, b, root }.ToDictionary(r => r.RepresentationId);
        var context = new HistoryExactCheckpointAdmission.EvaluationContext(versions, reps);
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, context.Evaluate(Checkpoint(tip)).Status);
        Assert.AreEqual(131L, BackupSizeMetadata.DependencyBytes(root, reps));
        Assert.AreEqual(110L, BackupSizeMetadata.DependencyBytes(a, reps));
        Assert.AreEqual(120L, BackupSizeMetadata.DependencyBytes(b, reps));
        Assert.AreEqual(131L, BackupSizeMetadata.DependencyBytes(root, reps));
        // One unavailable logical parent fails the AND, even with an independent Exact tip.
        var missingParent = reps.Where(pair => pair.Key != b.RepresentationId).ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing,
            new HistoryExactCheckpointAdmission.EvaluationContext(versions, missingParent).Evaluate(Checkpoint(tip)).Status);
        Assert.IsNull(BackupSizeMetadata.DependencyBytes(root, missingParent));
        var overflow = Representation(RepresentationId.New(), tip.VersionId, [shared.RepresentationId], bytes: long.MaxValue);
        Assert.IsNull(BackupSizeMetadata.DependencyBytes(overflow, reps));
        // The supplied root need not be registered in the dependency dictionary.
        var independent = Representation(RepresentationId.New(), tip.VersionId, [], bytes: 7);
        Assert.AreEqual(7L, BackupSizeMetadata.DependencyBytes(independent, reps));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationInsideAdmissionDoesNotMemoizeUnfinishedOrFailedNodes(bool partial)
    {
        var (versions, reps, tip) = Chain(80, partial);
        using var cancelled = new CancellationTokenSource();
        var versionReads = new ObservedDictionary<VersionId, SourceVersion>(versions,
            count => { if (partial && count == 25) cancelled.Cancel(); });
        var repReads = new ObservedDictionary<RepresentationId, VersionRepresentation>(reps,
            count => { if (!partial && count == 25) cancelled.Cancel(); });
        var context = new HistoryExactCheckpointAdmission.EvaluationContext(versionReads, repReads);
        var checkpoint = Checkpoint(tip);
        Assert.ThrowsExactly<OperationCanceledException>(() => context.Evaluate(checkpoint, cancelled.Token));
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, context.Evaluate(checkpoint).Status);
        // A cancelled caller must not receive even an already-completed checkpoint.
        Assert.ThrowsExactly<OperationCanceledException>(() => context.Evaluate(checkpoint, cancelled.Token));
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, context.Evaluate(Checkpoint(tip)).Status);
    }

    [TestMethod]
    public void AvailabilityCancellationAndRemotePermissionDoNotPolluteCompletedMemo()
    {
        var (_, reps, tip) = Chain(80, false);
        var root = reps.Values.Single(r => r.VersionId == tip.VersionId);
        using var cancelled = new CancellationTokenSource();
        var graph = new ObservedDictionary<RepresentationId, VersionRepresentation>(reps,
            count => { if (count == 25) cancelled.Cancel(); });
        var calls = 0;
        var context = new HistoryPresentationQueryService.AvailabilityContext(graph, (_, remote) => { calls++; return remote; });
        Assert.ThrowsExactly<OperationCanceledException>(() => context.IsAvailable(root, true, cancelled.Token));
        Assert.IsTrue(context.IsAvailable(root, true));
        var completedCalls = calls;
        Assert.IsTrue(context.IsAvailable(root, true));
        Assert.AreEqual(completedCalls, calls);
        Assert.IsFalse(context.IsAvailable(root, false));
        Assert.IsTrue(context.IsAvailable(root, true));
        Assert.ThrowsExactly<OperationCanceledException>(() => context.IsAvailable(root, true, cancelled.Token));
        var rejecting = new HistoryPresentationQueryService.AvailabilityContext(
            new ObservedDictionary<RepresentationId, VersionRepresentation>(reps, _ => Assert.Fail("Absent root must short-circuit dependencies.")),
            (_, _) => false);
        Assert.IsFalse(rejecting.IsAvailable(root, false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationDuringLeafPresenceCheckIsNotCached(bool initialPresence)
    {
        var leaf = Representation(RepresentationId.New(), VersionId.New(), []);
        using var cancelled = new CancellationTokenSource();
        var calls = 0;
        var context = new HistoryPresentationQueryService.AvailabilityContext(
            new Dictionary<RepresentationId, VersionRepresentation>(), (_, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    cancelled.Cancel();
                    return initialPresence;
                }
                return !initialPresence;
            });

        Assert.ThrowsExactly<OperationCanceledException>(() => context.IsAvailable(leaf, false, cancelled.Token));
        Assert.AreEqual(!initialPresence, context.IsAvailable(leaf, false));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(!initialPresence, context.IsAvailable(leaf, false));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void SizeTraversalCancellationPropagatesInsteadOfReturningUnknown()
    {
        var (_, reps, tip) = Chain(80, false);
        var root = reps.Values.Single(r => r.VersionId == tip.VersionId);
        using var cancelled = new CancellationTokenSource();
        var graph = new ObservedDictionary<RepresentationId, VersionRepresentation>(reps,
            count => { if (count == 25) cancelled.Cancel(); });
        Assert.ThrowsExactly<OperationCanceledException>(() => BackupSizeMetadata.DependencyBytes(root, graph, cancelled.Token));
        Assert.AreEqual(80L, BackupSizeMetadata.DependencyBytes(root, graph));
        Assert.ThrowsExactly<OperationCanceledException>(() => BackupSizeMetadata.DependencyBytes(null, graph, cancelled.Token));
    }

    private static (Dictionary<VersionId, SourceVersion>, Dictionary<RepresentationId, VersionRepresentation>, SourceVersion) Chain(int count, bool partial)
    {
        var versions = new List<SourceVersion>();
        var reps = new List<VersionRepresentation>();
        for (int i = 0; i < count; i++)
        {
            var version = Version(VersionId.New(), i == 0 ? [] : [versions[^1].VersionId], partial && i != 0);
            versions.Add(version);
            reps.Add(Representation(RepresentationId.New(), version.VersionId,
                partial || i == 0 ? [] : [reps[^1].RepresentationId]));
        }
        return (versions.ToDictionary(v => v.VersionId), reps.ToDictionary(r => r.RepresentationId), versions[^1]);
    }

    private static SourceVersion Version(VersionId id, IEnumerable<VersionId> parents, bool partial,
        SourceId? source = null, HistoryConfigId? config = null, EffectiveSourceBoundarySnapshot? boundary = null)
        => new(id, config ?? Config, source ?? Source, parents, Time, null,
            partial ? CaptureScope.PartialSource : CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new("source", "source"), null, HistoryProvenance.Native("test"), boundary);

    private static VersionRepresentation Representation(RepresentationId id, VersionId version, IEnumerable<RepresentationId> dependencies,
        MaterializationFidelity fidelity = MaterializationFidelity.Exact, long? bytes = 1)
        => new(id, version, RepresentationKind.CoreFull, "test", dependencies, fidelity, null, null,
            bytes is { } value ? new Dictionary<string, string> { [BackupSizeMetadata.StoredBytesKey] = BackupSizeMetadata.FormatBytes(value) } : null);

    private static SourceCheckpoint Checkpoint(SourceVersion version)
        => new(CheckpointId.New(), version.ConfigId, Time, null, HistoryProvenance.Native("test"),
            [new(version.SourceId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured, version.EffectiveSourceBoundary)]);

    private static HistoryExactCheckpointAdmissionStatus OracleAdmission(SourceVersion version,
        IReadOnlyDictionary<VersionId, SourceVersion> versions, IReadOnlyDictionary<RepresentationId, VersionRepresentation> reps)
    {
        bool Exact(VersionRepresentation node, HashSet<RepresentationId> visiting)
        {
            if (!visiting.Add(node.RepresentationId)) return false;
            try { return node.Fidelity == MaterializationFidelity.Exact && node.DependencyRepresentationIds.All(id => reps.TryGetValue(id, out var dependency) && Exact(dependency, visiting)); }
            finally { visiting.Remove(node.RepresentationId); }
        }
        bool ExactVersion(VersionId id) => reps.Values.Where(r => r.VersionId == id).Any(root => Exact(root, []));
        bool Reliable(SourceVersion node, HashSet<VersionId> visiting)
        {
            if (!visiting.Add(node.VersionId)) return false;
            try
            {
                return !node.ParentVersionIds.IsEmpty && node.ParentVersionIds.All(id => versions.TryGetValue(id, out var parent)
                    && parent.ConfigId == node.ConfigId && parent.SourceId == node.SourceId
                    && parent.EffectiveSourceBoundary.Fingerprint == node.EffectiveSourceBoundary.Fingerprint
                    && (parent.CaptureScope != CaptureScope.PartialSource || Reliable(parent, visiting)) && ExactVersion(id));
            }
            finally { visiting.Remove(node.VersionId); }
        }
        if (version.CaptureScope == CaptureScope.PartialSource && !Reliable(version, []))
            return HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing;
        return ExactVersion(version.VersionId) ? HistoryExactCheckpointAdmissionStatus.Ready : HistoryExactCheckpointAdmissionStatus.ExactRepresentationUnavailable;
    }

    private static bool OracleAvailable(VersionRepresentation node, IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph,
        Func<VersionRepresentation, bool, bool> present, bool remote, HashSet<RepresentationId> visiting)
    {
        if (!visiting.Add(node.RepresentationId)) return false;
        try { return present(node, remote) && node.DependencyRepresentationIds.All(id => graph.TryGetValue(id, out var dependency) && OracleAvailable(dependency, graph, present, remote, visiting)); }
        finally { visiting.Remove(node.RepresentationId); }
    }

    private static long? OracleBytes(VersionRepresentation root, IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
    {
        var visited = new HashSet<RepresentationId>();
        var visiting = new HashSet<RepresentationId>();
        long sum = 0;
        bool Visit(VersionRepresentation node)
        {
            if (visited.Contains(node.RepresentationId)) return true;
            if (!visiting.Add(node.RepresentationId)) return false;
            var bytes = BackupSizeMetadata.ReadBytes(node.RepresentationSpecificMetadata,
                node.Kind == RepresentationKind.PluginArtifact ? BackupSizeMetadata.NativeClosureBytesKey : BackupSizeMetadata.StoredBytesKey);
            if (bytes is null) return false;
            sum = checked(sum + bytes.Value);
            foreach (var id in node.DependencyRepresentationIds)
                if (!graph.TryGetValue(id, out var child) || !Visit(child)) return false;
            visiting.Remove(node.RepresentationId);
            visited.Add(node.RepresentationId);
            return true;
        }
        try { return Visit(root) ? sum : null; }
        catch (OverflowException) { return null; }
    }

    private sealed class ObservedDictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> inner, Action<int> read)
        : IReadOnlyDictionary<TKey, TValue> where TKey : notnull
    {
        private int _reads;
        public TValue this[TKey key] => inner[key];
        public IEnumerable<TKey> Keys => inner.Keys;
        public IEnumerable<TValue> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(TKey key) => inner.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value)
        {
            read(++_reads);
            return inner.TryGetValue(key, out value!);
        }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
