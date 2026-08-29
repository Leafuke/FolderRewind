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

    [TestMethod]
    public async Task VerifiedArchiveFactoryCreatesFinalCandidatesAndCleanupHandle()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindVerifiedCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var payloadPath = Path.Combine(root, "capture.7z");
        await File.WriteAllTextAsync(payloadPath, "verified payload");
        try
        {
            var capture = VerifiedArchiveCaptureFactory.Create(
                SourceId.New(),
                CaptureScope.FullSource,
                payloadPath,
                RepresentationKind.CoreFull,
                "7z",
                new Dictionary<string, SourceCaptureFileState>
                {
                    ["file.txt"] = new(4, DateTime.UnixEpoch)
                },
                baseline: null,
                dependencies: [],
                consecutiveSmartCaptures: 0);

            Assert.IsNull(capture.StateFingerprint);
            Assert.IsNull(capture.RepresentationCandidate!.StateFingerprint);
            Assert.AreEqual(CapturePayloadState.VerifiedFinal, capture.PayloadCandidate!.State);
            Assert.AreEqual(CapturePayloadState.VerifiedFinal, capture.LocalReplicaCandidate!.PayloadState);
            Assert.IsNotNull(capture.CleanupHandle);

            await capture.CleanupHandle.CleanupAsync(CancellationToken.None);
            Assert.IsFalse(File.Exists(payloadPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
