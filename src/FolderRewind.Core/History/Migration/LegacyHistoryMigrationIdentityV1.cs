using FolderRewind.History.Domain;
using FolderRewind.Plugin.Runtime.Configuration;
using System;

namespace FolderRewind.History.Migration;

public static class LegacyHistoryMigrationIdentityV1
{
    public static readonly Guid Namespace = Guid.Parse("0fb2dbe8-f52e-5a92-9a77-cec76166621c");

    public static Guid Create(string kind, string stableKey)
        => LegacySourceIdentityV1.CreateUuid5(Namespace, $"legacy-history-migration-v1|{kind}|{stableKey}");

    public static VersionId Version(string originKey) => new(Create("version", originKey));
    public static RepresentationId Representation(string originKey) => new(Create("representation", originKey));
    public static LegacyMigrationRecordId Record(string originKey) => new(Create("record", originKey));
    public static AnnotationUpdateId Annotation(string originKey, HistoryAnnotationKind kind)
        => new(Create($"annotation-{(int)kind}", originKey));
    public static ReplicaId CloudReplica(string originKey) => new(Create("cloud-replica", originKey));
    public static ReplicaLifecycleUpdateId CloudLifecycle(string originKey) => new(Create("cloud-lifecycle", originKey));
    public static LocalReplicaId LocalReplica(string originKey) => new(Create("local-replica", originKey));
    public static CheckpointId Checkpoint(string configAndVector) => new(Create("checkpoint", configAndVector));
    public static BranchId LegacyMain(HistoryConfigId configId) => new(Create("branch", configId + "|legacy-main"));
    public static BranchUpdateId BranchUpdate(BranchId branchId, CheckpointId checkpointId)
        => new(Create("branch-update", branchId + "|" + checkpointId));
}
