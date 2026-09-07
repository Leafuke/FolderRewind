using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryExactCheckpointAdmissionTests
{
    [TestMethod]
    public void KnownEmptyRoster_IsAnExactCheckpoint()
    {
        var checkpoint = Checkpoint([]);

        var result = HistoryExactCheckpointAdmission.Evaluate(
            checkpoint,
            new Dictionary<VersionId, SourceVersion>(),
            new Dictionary<RepresentationId, VersionRepresentation>());

        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.Ready, result.Status);
    }

    [TestMethod]
    public void NullVersion_IsNotDeletionOrExactState()
    {
        var checkpoint = Checkpoint([
            new CheckpointSource(
                SourceId.New(),
                new SourceDescriptorSnapshot("source", "source"),
                null,
                CheckpointSourceDisposition.Unavailable)
        ]);

        var result = HistoryExactCheckpointAdmission.Evaluate(
            checkpoint,
            new Dictionary<VersionId, SourceVersion>(),
            new Dictionary<RepresentationId, VersionRepresentation>());

        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.StructurallyIncomplete, result.Status);
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

    private static ConfigurationCheckpoint Checkpoint(IEnumerable<CheckpointSource> sources)
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
