using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryExactCheckpointAdmissionTests
{
    [TestMethod]
    public void EmptyFileTreeWithVersion_IsAnExactCheckpoint()
    {
        var version = Version(SourceId.New(), CaptureScope.FullSource, [], SourceVersionCreationKind.Capture);
        var representation = Representation(version.VersionId, MaterializationFidelity.Exact, []);
        var checkpoint = Checkpoint([new(version.SourceId, version.SourceDescriptorSnapshot,
            version.VersionId, CheckpointSourceDisposition.Captured)]);

        var result = HistoryExactCheckpointAdmission.Evaluate(
            checkpoint,
            new Dictionary<VersionId, SourceVersion> { [version.VersionId] = version },
            new Dictionary<RepresentationId, VersionRepresentation> { [representation.RepresentationId] = representation });

        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, result.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => Checkpoint([]));
    }

    [TestMethod]
    public void NullVersion_IsNotDeletionOrExactState()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Checkpoint([
            new CheckpointSource(
                SourceId.New(),
                new SourceDescriptorSnapshot("source", "source"),
                null,
                CheckpointSourceDisposition.Unavailable)
        ]));
    }

    [TestMethod]
    public void ParentlessPartialImport_CannotEnterExactMergeOrBranchTip()
    {
        var sourceId = SourceId.New();
        var version = Version(sourceId, CaptureScope.PartialSource, [], SourceVersionCreationKind.Import);
        var representation = Representation(version.VersionId, MaterializationFidelity.Exact, []);
        var checkpoint = Checkpoint([
            new CheckpointSource(
                sourceId,
                version.SourceDescriptorSnapshot,
                version.VersionId,
                CheckpointSourceDisposition.CarriedForward,
                version.EffectiveSourceBoundary)
        ]);

        var result = HistoryExactCheckpointAdmission.Evaluate(
            checkpoint,
            new Dictionary<VersionId, SourceVersion> { [version.VersionId] = version },
            new Dictionary<RepresentationId, VersionRepresentation> { [representation.RepresentationId] = representation });

        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing, result.Status);
    }

    [TestMethod]
    public void ExactRepresentationDependencyClosure_IsRequired()
    {
        var sourceId = SourceId.New();
        var version = Version(sourceId, CaptureScope.FullSource, [], SourceVersionCreationKind.Capture);
        var missingDependency = RepresentationId.New();
        var representation = Representation(
            version.VersionId,
            MaterializationFidelity.Exact,
            [missingDependency]);
        var checkpoint = Checkpoint([
            new CheckpointSource(
                sourceId,
                version.SourceDescriptorSnapshot,
                version.VersionId,
                CheckpointSourceDisposition.Captured,
                version.EffectiveSourceBoundary)
        ]);

        var result = HistoryExactCheckpointAdmission.Evaluate(
            checkpoint,
            new Dictionary<VersionId, SourceVersion> { [version.VersionId] = version },
            new Dictionary<RepresentationId, VersionRepresentation> { [representation.RepresentationId] = representation });

        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.ExactRepresentationUnavailable, result.Status);
    }

    [TestMethod]
    public void PartialSourceWithNonExactParent_IsRejectedEvenWhenRepackedAsExact()
    {
        var sourceId = SourceId.New();
        var parent = Version(sourceId, CaptureScope.FullSource, [], SourceVersionCreationKind.Import);
        var child = Version(sourceId, CaptureScope.PartialSource, [parent.VersionId], SourceVersionCreationKind.Capture);
        var parentRepresentation = Representation(parent.VersionId, MaterializationFidelity.Partial, []);
        var childRepresentation = Representation(child.VersionId, MaterializationFidelity.Exact, []);
        var checkpoint = Checkpoint([new CheckpointSource(sourceId, child.SourceDescriptorSnapshot,
            child.VersionId, CheckpointSourceDisposition.Captured, child.EffectiveSourceBoundary)]);
        var result = HistoryExactCheckpointAdmission.Evaluate(checkpoint,
            new[] { parent, child }.ToDictionary(v => v.VersionId),
            new[] { parentRepresentation, childRepresentation }.ToDictionary(r => r.RepresentationId));
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing, result.Status);
    }

    [TestMethod]
    public void SharedVersionStillChecksEachCheckpointIdentityAndBoundary()
    {
        var version = Version(SourceId.New(), CaptureScope.FullSource, [], SourceVersionCreationKind.Capture);
        var representation = Representation(version.VersionId, MaterializationFidelity.Exact, []);
        var valid = Checkpoint([new(version.SourceId, version.SourceDescriptorSnapshot,
            version.VersionId, CheckpointSourceDisposition.Captured)]);
        var differentBoundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.Include, ["saves/**"],
            EffectiveBoundaryFilterMode.Blacklist, [], false);
        // Even an independently supplied checkpoint with the same ID must not reuse the
        // original object's admission when its immutable facts differ.
        var mismatched = new SourceCheckpoint(valid.CheckpointId, valid.ConfigId, valid.CreatedAtUtc, null,
            valid.Origin, [new(version.SourceId, version.SourceDescriptorSnapshot, version.VersionId,
                CheckpointSourceDisposition.Captured, differentBoundary)]);
        var wrongSource = Checkpoint([new(SourceId.New(), version.SourceDescriptorSnapshot,
            version.VersionId, CheckpointSourceDisposition.Captured)]);
        var context = new HistoryExactCheckpointAdmission.EvaluationContext(
            new[] { version }.ToDictionary(v => v.VersionId), new[] { representation }.ToDictionary(r => r.RepresentationId));

        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, context.Evaluate(valid).Status);
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.BoundaryMismatch, context.Evaluate(mismatched).Status);
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.VersionIdentityMismatch, context.Evaluate(wrongSource).Status);
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, context.Evaluate(valid).Status);
    }

    [TestMethod]
    public void SharedExactDependenciesAndInvalidAlternativesKeepBranchabilitySemantics()
    {
        var source = SourceId.New();
        var first = Version(source, CaptureScope.FullSource, [], SourceVersionCreationKind.Capture);
        var second = Version(source, CaptureScope.FullSource, [first.VersionId], SourceVersionCreationKind.Capture);
        var third = Version(source, CaptureScope.FullSource, [second.VersionId], SourceVersionCreationKind.Capture);
        var shared = Representation(first.VersionId, MaterializationFidelity.Exact, []);
        var secondExact = Representation(second.VersionId, MaterializationFidelity.Exact, [shared.RepresentationId]);
        var thirdExact = Representation(third.VersionId, MaterializationFidelity.Exact, [shared.RepresentationId, secondExact.RepresentationId]);
        var cycleId = RepresentationId.New();
        var cycle = new VersionRepresentation(cycleId, third.VersionId, RepresentationKind.CoreFull, "test", [cycleId],
            MaterializationFidelity.Exact, null, null, null);
        var versions = new[] { first, second, third }.ToDictionary(v => v.VersionId);
        var context = new HistoryExactCheckpointAdmission.EvaluationContext(versions,
            new[] { cycle, shared, secondExact, thirdExact }.ToDictionary(r => r.RepresentationId));
        foreach (var version in new[] { third, second, first })
            Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready,
                context.Evaluate(Checkpoint([new(source, version.SourceDescriptorSnapshot, version.VersionId,
                    CheckpointSourceDisposition.Captured)])).Status);

        var changedContext = new HistoryExactCheckpointAdmission.EvaluationContext(versions,
            new[] { cycle, shared, secondExact }.ToDictionary(r => r.RepresentationId));
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.ExactRepresentationUnavailable,
            changedContext.Evaluate(Checkpoint([new(source, third.SourceDescriptorSnapshot, third.VersionId,
                CheckpointSourceDisposition.Captured)])).Status);
    }

    [TestMethod]
    public void PartialLogicalParentMemoPreservesBoundaryFailures()
    {
        var source = SourceId.New();
        var full = Version(source, CaptureScope.FullSource, [], SourceVersionCreationKind.Capture);
        var partial = Version(source, CaptureScope.PartialSource, [full.VersionId], SourceVersionCreationKind.Capture);
        var descendant = Version(source, CaptureScope.PartialSource, [partial.VersionId], SourceVersionCreationKind.Capture);
        var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.Include, ["data/**"],
            EffectiveBoundaryFilterMode.Blacklist, [], false);
        var other = new SourceVersion(VersionId.New(), full.ConfigId, source, [partial.VersionId], DateTimeOffset.UtcNow, null,
            CaptureScope.PartialSource, CaptureOutcome.Captured, [], full.SourceDescriptorSnapshot, null,
            HistoryProvenance.Native("test"), boundary);
        var versions = new[] { full, partial, descendant, other };
        var context = new HistoryExactCheckpointAdmission.EvaluationContext(versions.ToDictionary(v => v.VersionId),
            versions.Select(v => Representation(v.VersionId, MaterializationFidelity.Exact, [])).ToDictionary(r => r.RepresentationId));
        foreach (var version in new[] { descendant, partial, other, descendant })
            Assert.AreEqual(version == other ? HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing : HistoryExactCheckpointAdmissionStatus.Ready,
                context.Evaluate(Checkpoint([new(source, version.SourceDescriptorSnapshot, version.VersionId,
                    CheckpointSourceDisposition.Captured, version.EffectiveSourceBoundary)])).Status);
    }

    private static SourceCheckpoint Checkpoint(IEnumerable<CheckpointSource> sources)
        => new(
            CheckpointId.New(),
            new HistoryConfigId("config"),
            DateTimeOffset.UtcNow,
            null,
            HistoryProvenance.Native("test"),
            sources);

    private static SourceVersion Version(
        SourceId sourceId,
        CaptureScope scope,
        IEnumerable<VersionId> parents,
        SourceVersionCreationKind creationKind)
        => new(
            VersionId.New(),
            new HistoryConfigId("config"),
            sourceId,
            parents,
            DateTimeOffset.UtcNow,
            null,
            scope,
            CaptureOutcome.Captured,
            [],
            new SourceDescriptorSnapshot("source", "source"),
            null,
            HistoryProvenance.Native("test"),
            creationKind: creationKind);

    private static VersionRepresentation Representation(
        VersionId versionId,
        MaterializationFidelity fidelity,
        IEnumerable<RepresentationId> dependencies)
        => new(
            RepresentationId.New(),
            versionId,
            RepresentationKind.CoreFull,
            "test",
            dependencies,
            fidelity,
            null,
            null,
            null);
}
