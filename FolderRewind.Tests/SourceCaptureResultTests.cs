using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SourceCaptureResultTests
{
    [TestMethod]
    public void SmartCandidateCarriesStableRepresentationDependency()
    {
        var dependencyId = RepresentationId.New();
        var representation = new RepresentationCandidate(
            RepresentationId.New(),
            RepresentationKind.CoreSmartDelta,
            "smart-v1",
            [dependencyId],
            RestoreStrategy.Exact,
            null,
            "fingerprint",
            null);

        CollectionAssert.AreEqual(
            new[] { dependencyId },
            representation.DependencyRepresentationIds.ToArray());
        Assert.IsFalse(representation.Metadata.ContainsKey("previousBackupFileName"));
    }

    [TestMethod]
    public void CapturedResultRequiresDurableCandidateShape()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SourceCaptureResult(
            SourceId.New(),
            SourceCaptureOutcome.Captured,
            CaptureScope.FullSource,
            null,
            null,
            null,
            null,
            null,
            expectedWorkspaceRevision: 0,
            expectedBaseVersionId: null,
            cleanupHandle: null,
            diagnostics: []));
    }

    [TestMethod]
    public void LegacyBridgeCandidateCannotEnterNativeRepository()
    {
        var candidate = new RepresentationCandidate(
            RepresentationId.New(),
            RepresentationKind.LegacyArchive,
            "7z",
            [],
            RestoreStrategy.Overlay,
            null,
            null,
            null,
            isLegacyBridgeCandidate: true);

        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.ToFact(VersionId.New()));
    }
}
