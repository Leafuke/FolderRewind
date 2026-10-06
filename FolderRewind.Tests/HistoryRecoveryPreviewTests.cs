using FolderRewind.History.Domain;
using FolderRewind.History.Representation;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRecoveryPreviewTests
{
    [TestMethod]
    [DataRow(CaptureScope.FullSource, MaterializationFidelity.Exact)]
    [DataRow(CaptureScope.PartialSource, MaterializationFidelity.Partial)]
    public void PreviewDeduplicatesClosureAndExcludesCachedPayloads(CaptureScope scope, MaterializationFidelity fidelity)
    {
        var version = Version(scope);
        var shared = Rep(VersionId.New(), fidelity);
        var cached = Rep(VersionId.New(), fidelity, shared.RepresentationId);
        var root = Rep(version.VersionId, fidelity, shared.RepresentationId, cached.RepresentationId);
        var graph = new[] { shared, cached, root }.ToDictionary(r => r.RepresentationId);
        var assessment = new VersionAssessment(version.VersionId, AssessmentDepth.Fast, fidelity,
            HistoryReadiness.PreparationRequired, new(root.RepresentationId, HistoryReadiness.PreparationRequired, fidelity, [], []), []);
        var preview = HistoryRecoveryPreview.Create(version, graph, assessment, id => id == cached.RepresentationId, _ => 8L);
        Assert.IsTrue(preview.CanPrepare);
        Assert.AreEqual(fidelity, preview.RequiredFidelity);
        Assert.AreEqual(16L, preview.DownloadBytes);
        CollectionAssert.AreEqual(new[] { shared.RepresentationId, root.RepresentationId }, preview.MissingPayloads.Select(p => p.RepresentationId).ToArray());
        var missing = HistoryRecoveryPreview.Create(version, graph, assessment, _ => false,
            id => id == shared.RepresentationId ? null : 8L);
        Assert.IsFalse(missing.CanPrepare);
        Assert.IsNull(missing.DownloadBytes);
    }

    [TestMethod]
    public void UnknownHandlersAndDifferentScopeCannotAuthorizeRecovery()
    {
        var version = Version(CaptureScope.FullSource);
        var assessment = new VersionAssessment(version.VersionId, AssessmentDepth.Fast, MaterializationFidelity.Exact,
            HistoryReadiness.Blocked, null, []);
        Assert.IsFalse(HistoryRecoveryPreview.Create(version, new Dictionary<RepresentationId, VersionRepresentation>(), assessment, _ => false, _ => 8L).CanPrepare);
        Assert.ThrowsExactly<ArgumentException>(() => HistoryRecoveryPreview.Create(version,
            new Dictionary<RepresentationId, VersionRepresentation>(), assessment with { RequiredFidelity = MaterializationFidelity.Partial }, _ => false, _ => 8L));
    }

    private static SourceVersion Version(CaptureScope scope) => new(VersionId.New(), new("preview-test"), SourceId.New(), [],
        DateTimeOffset.UtcNow, null, scope, CaptureOutcome.Recovered, [], new("test source", ""), null, HistoryProvenance.Native("test"));
    private static VersionRepresentation Rep(VersionId version, MaterializationFidelity fidelity, params RepresentationId[] deps)
        => new(RepresentationId.New(), version, RepresentationKind.CoreFull, "7z", deps, fidelity, null, null, null);
}
