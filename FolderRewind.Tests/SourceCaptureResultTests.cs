using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System.Collections.Immutable;

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
            MaterializationFidelity.Exact,
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
            MaterializationFidelity.Partial,
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
                consecutiveSmartCaptures: 0,
                fidelity: MaterializationFidelity.Exact);

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

    [TestMethod]
    public async Task PartialPatchBaselineMergesChangesAndDeletesWithoutDroppingOutsideScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindVerifiedCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var payloadPath = Path.Combine(root, "delta.7z");
        await File.WriteAllTextAsync(payloadPath, "verified delta");
        var unchanged = new SourceCaptureFileState(1, DateTime.UnixEpoch);
        var baseline = new SourceCaptureBaseline(
            SourceId.New(),
            2,
            VersionId.New(),
            RepresentationId.New(),
            RepresentationKind.CoreFull,
            Path.Combine(root, "base.7z"),
            0,
            new Dictionary<string, SourceCaptureFileState>
            {
                ["selected/changed.txt"] = unchanged,
                ["selected/deleted.txt"] = unchanged,
                ["outside/keep.txt"] = unchanged
            }.ToImmutableSortedDictionary(StringComparer.Ordinal));
        try
        {
            var capture = VerifiedArchiveCaptureFactory.Create(
                baseline.SourceId,
                CaptureScope.PartialSource,
                payloadPath,
                RepresentationKind.CoreSmartDelta,
                "7z",
                new Dictionary<string, SourceCaptureFileState>
                {
                    ["selected/changed.txt"] = new(2, DateTime.UnixEpoch.AddSeconds(1))
                },
                baseline,
                [baseline.BaseRepresentationId],
                1,
                MaterializationFidelity.Exact,
                baseline.BaseVersionId,
                ["selected/deleted.txt"]);

            var states = capture.BaselineCandidate!.FileStates;
            Assert.AreEqual(2, states["selected/changed.txt"].Size);
            Assert.IsFalse(states.ContainsKey("selected/deleted.txt"));
            Assert.IsTrue(states.ContainsKey("outside/keep.txt"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
