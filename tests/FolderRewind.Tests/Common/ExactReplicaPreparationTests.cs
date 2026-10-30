using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ExactReplicaPreparationTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("download-failed")]
    [DataRow("cancelled")]
    [DataRow("unavailable")]
    [DataRow("deep-failed")]
    public async Task ExplicitPreparationDeduplicatesAndVerifiesDependencyClosure(string scenario)
    {
        var source = SourceId.New(); var first = VersionId.New(); var second = VersionId.New();
        VersionRepresentation Rep(VersionId version, params RepresentationId[] deps) => new(RepresentationId.New(), version,
            RepresentationKind.CoreFull, "zip", deps, MaterializationFidelity.Exact, null, null, null);
        var dependency = Rep(VersionId.New()); var a = Rep(first, dependency.RepresentationId); var b = Rep(second, dependency.RepresentationId);
        var map = new[] { dependency, a, b }.ToDictionary(r => r.RepresentationId);
        var downloads = new List<RepresentationId>(); var deep = new List<VersionId>();
        using var cancellation = new CancellationTokenSource();
        Task<VersionAssessment> Assess(VersionId version, AssessmentDepth depth, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (depth == AssessmentDepth.Deep) deep.Add(version);
            var readiness = scenario == "unavailable" || depth == AssessmentDepth.Deep && scenario == "deep-failed"
                ? HistoryReadiness.Unavailable : depth == AssessmentDepth.Deep ? HistoryReadiness.Ready : HistoryReadiness.PreparationRequired;
            return Task.FromResult(new VersionAssessment(version, depth, MaterializationFidelity.Exact, readiness,
                new(version == first ? a.RepresentationId : b.RepresentationId, readiness, MaterializationFidelity.Exact, [], []), []));
        }
        Task Prepare() => ExactReplicaPreparation.PrepareAsync([(source, first), (source, second), (source, first)], map,
            Assess, _ => false, (_, representation, _) =>
            {
                downloads.Add(representation.RepresentationId);
                if (scenario == "cancelled") cancellation.Cancel();
                return Task.FromResult(scenario != "download-failed");
            }, cancellation.Token);
        if (scenario == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(Prepare);
        else if (scenario != "success") await Assert.ThrowsExactlyAsync<HistoryMergeBlockedException>(Prepare);
        else
        {
            await Prepare();
            CollectionAssert.AreEqual(new[] { dependency.RepresentationId, a.RepresentationId, b.RepresentationId }, downloads);
            CollectionAssert.AreEqual(new[] { first, second }, deep);
        }
        if (scenario == "unavailable") Assert.IsEmpty(downloads);
        if (scenario is "download-failed" or "cancelled") Assert.HasCount(1, downloads);
    }

    [TestMethod]
    public void MissingDependencyCannotBeSilentlySkipped()
    {
        var root = new VersionRepresentation(RepresentationId.New(), VersionId.New(), RepresentationKind.CoreFull,
            "zip", [RepresentationId.New()], MaterializationFidelity.Exact, null, null, null);
        Assert.ThrowsExactly<InvalidDataException>(() => ExactReplicaPreparation.Closure(root, new Dictionary<RepresentationId, VersionRepresentation>()));
    }
}
