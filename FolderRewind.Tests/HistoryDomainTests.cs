using FolderRewind.History.Domain;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryDomainTests
{
    [TestMethod]
    public void StrongIds_RoundTripAsCanonicalStrings()
    {
        var id = VersionId.New();
        var json = JsonSerializer.Serialize(id);
        var roundTrip = JsonSerializer.Deserialize<VersionId>(json);

        Assert.AreEqual($"\"{id.Value:N}\"", json);
        Assert.AreEqual(id, roundTrip);
        Assert.ThrowsExactly<FormatException>(() => VersionId.Parse(Guid.Empty.ToString()));
    }

    [TestMethod]
    public void DomainObject_RoundTripsWithoutMutableSerializationShape()
    {
        var version = CreateVersion([VersionId.New()]);

        var json = JsonSerializer.Serialize(version);
        var roundTrip = JsonSerializer.Deserialize<SourceVersion>(json);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(version.VersionId, roundTrip.VersionId);
        CollectionAssert.AreEqual(version.ParentVersionIds.ToArray(), roundTrip.ParentVersionIds.ToArray());
    }

    [TestMethod]
    public void AllRepositoryDomainObjects_HaveStableRoundTripShapes()
    {
        var version = CreateVersion([]);
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), version.ConfigId, DateTimeOffset.UtcNow, null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(version.SourceId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);
        var representation = new VersionRepresentation(
            RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "7z", [],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        var replica = new StorageReplica(
            ReplicaId.New(), representation.RepresentationId, ReplicaProviderKind.Cloud,
            "replicas/object", 12, null, HistoryProvenance.Native("test"));
        var lifecycle = new ReplicaLifecycleUpdate(
            ReplicaLifecycleUpdateId.New(), replica.ReplicaId, [], ReplicaLifecycleState.Active,
            DateTimeOffset.UtcNow, "created");
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var annotation = new HistoryAnnotationUpdate(
            AnnotationUpdateId.New(),
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.VersionId.Value),
            HistoryAnnotationKind.Pin, [], "true", DateTimeOffset.UtcNow);
        var policy = new MaterializationPolicyUpdate(
            MaterializationPolicyUpdateId.New(), version.VersionId, [], MaterializationPolicyState.Retained,
            DateTimeOffset.UtcNow, "retain");
        var run = new BackupRun(
            RunId.New(), version.ConfigId, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow,
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(version.SourceId, BackupRunSourceOutcome.Captured, version.VersionId, [])],
            checkpoint.CheckpointId, []);

        foreach (var value in new object[]
                 {
                     version, checkpoint, representation, replica, lifecycle, branch, annotation, policy, run
                 })
        {
            var json = JsonSerializer.Serialize(value, value.GetType());
            var roundTrip = JsonSerializer.Deserialize(json, value.GetType());
            Assert.IsNotNull(roundTrip, value.GetType().Name);
            Assert.AreEqual(json, JsonSerializer.Serialize(roundTrip, value.GetType()), value.GetType().Name);
        }
    }

    [TestMethod]
    public void NativeRepresentationHasNoLegacyHistoryBindingFields()
    {
        var propertyNames = typeof(VersionRepresentation)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("HistoryItemId", propertyNames);
        Assert.DoesNotContain("GraphRevision", propertyNames);
    }

    [TestMethod]
    public void ConfigId_UsesOneCanonicalContract()
    {
        var guid = Guid.NewGuid();

        Assert.AreEqual(guid.ToString("N"), HistoryConfigId.Parse($" {guid:D} ").Value);
        Assert.AreEqual("LEGACY-CONFIG", HistoryConfigId.Parse(" legacy-config ").Value);
    }

    [TestMethod]
    public void NativeVersion_RejectsMultipleParents()
    {
        var version = CreateVersion([VersionId.New(), VersionId.New()]);

        Assert.ThrowsExactly<HistoryDomainValidationException>(
            () => HistoryDomainValidator.ValidateNative(version));
    }

    [TestMethod]
    public void Checkpoint_AllowsUnavailableSourceWithoutVersion()
    {
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(),
            new HistoryConfigId("legacy"),
            DateTimeOffset.UtcNow,
            null,
            HistoryProvenance.Native("test"),
            [new CheckpointSource(
                SourceId.New(),
                new SourceDescriptorSnapshot("source", "C:\\source"),
                null,
                CheckpointSourceDisposition.Unavailable)]);

        Assert.IsFalse(checkpoint.IsStructurallyComplete);
    }

    [TestMethod]
    public void Branch_AllowsUnbornCreation()
    {
        var branch = new BranchUpdate(
            BranchUpdateId.New(),
            BranchId.New(),
            [],
            "main",
            null,
            false,
            DateTimeOffset.UtcNow,
            BranchUpdateReason.Created);

        HistoryDomainValidator.ValidateNative(branch);
        Assert.IsTrue(branch.IsUnborn);
    }

    [TestMethod]
    public void AnnotationGraph_RejectsCrossKindParent()
    {
        var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, Guid.NewGuid());
        var parent = new HistoryAnnotationUpdate(
            AnnotationUpdateId.New(), target, HistoryAnnotationKind.Pin, [], "true", DateTimeOffset.UtcNow);
        var child = new HistoryAnnotationUpdate(
            AnnotationUpdateId.New(), target, HistoryAnnotationKind.Comment, [parent.UpdateId], "note", DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<HistoryDomainValidationException>(
            () => HistoryDomainValidator.ValidateAnnotationGraph([parent, child]));
    }

    [TestMethod]
    public void PolicyAndLifecycleGraphs_RejectCrossTargetParents()
    {
        var policyParent = new MaterializationPolicyUpdate(
            MaterializationPolicyUpdateId.New(), VersionId.New(), [], MaterializationPolicyState.Retained,
            DateTimeOffset.UtcNow, "retain");
        var policyChild = new MaterializationPolicyUpdate(
            MaterializationPolicyUpdateId.New(), VersionId.New(), [policyParent.UpdateId], MaterializationPolicyState.Released,
            DateTimeOffset.UtcNow, "release");

        var lifecycleParent = new ReplicaLifecycleUpdate(
            ReplicaLifecycleUpdateId.New(), ReplicaId.New(), [], ReplicaLifecycleState.Active,
            DateTimeOffset.UtcNow, "created");
        var lifecycleChild = new ReplicaLifecycleUpdate(
            ReplicaLifecycleUpdateId.New(), ReplicaId.New(), [lifecycleParent.UpdateId], ReplicaLifecycleState.Retired,
            DateTimeOffset.UtcNow, "deleted");

        Assert.ThrowsExactly<HistoryDomainValidationException>(
            () => HistoryDomainValidator.ValidateMaterializationPolicyGraph([policyParent, policyChild]));
        Assert.ThrowsExactly<HistoryDomainValidationException>(
            () => HistoryDomainValidator.ValidateReplicaLifecycleGraph([lifecycleParent, lifecycleChild]));
    }

    private static SourceVersion CreateVersion(IEnumerable<VersionId> parents)
        => new(
            VersionId.New(),
            new HistoryConfigId("config"),
            SourceId.New(),
            parents,
            DateTimeOffset.UtcNow,
            null,
            CaptureScope.FullSource,
            CaptureOutcome.Captured,
            [],
            new SourceDescriptorSnapshot("source", "C:\\source"),
            null,
            HistoryProvenance.Native("test"));
}
