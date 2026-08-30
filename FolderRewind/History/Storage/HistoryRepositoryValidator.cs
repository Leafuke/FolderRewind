using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.History.Storage;

public sealed class HistoryRepositoryValidator
{
    private readonly HistoryPackCodec _codec;

    public HistoryRepositoryValidator(HistoryPackCodec codec)
        => _codec = codec ?? throw new ArgumentNullException(nameof(codec));

    public void Validate(
        HistoryConfigId configId,
        IEnumerable<HistoryPackReadResult> packs)
    {
        var objectMap = BuildObjectMap(packs);
        var known = objectMap.Values
            .Where(item => HistoryObjectKinds.IsKnown(item.Kind) && item.SchemaVersion == 1)
            .Select(_codec.DeserializeKnown)
            .ToArray();

        var versions = known.OfType<SourceVersion>().ToDictionary(item => item.VersionId);
        var checkpoints = known.OfType<ConfigurationCheckpoint>().ToDictionary(item => item.CheckpointId);
        var representations = known.OfType<VersionRepresentation>().ToDictionary(item => item.RepresentationId);
        var replicas = known.OfType<StorageReplica>().ToDictionary(item => item.ReplicaId);
        var lifecycles = known.OfType<ReplicaLifecycleUpdate>().ToDictionary(item => item.UpdateId);
        var branchUpdates = known.OfType<BranchUpdate>().ToDictionary(item => item.UpdateId);
        var runs = known.OfType<BackupRun>().ToDictionary(item => item.RunId);
        var annotations = known.OfType<HistoryAnnotationUpdate>().ToDictionary(item => item.UpdateId);
        var policies = known.OfType<MaterializationPolicyUpdate>().ToDictionary(item => item.UpdateId);
        var migrationRecords = known.OfType<LegacyMigrationRecord>().ToDictionary(item => item.RecordId);
        var safetySnapshots = known.OfType<SafetySnapshot>().ToDictionary(item => item.SnapshotId);
        var safetySnapshotReleases = known.OfType<SafetySnapshotRelease>().ToDictionary(item => item.ReleaseId);

        foreach (var version in versions.Values)
        {
            RequireConfig(configId, version.ConfigId, HistoryObjectKinds.SourceVersion, version.VersionId.ToString());
            HistoryDomainValidator.ValidateNative(version);
            foreach (var parentId in version.ParentVersionIds)
            {
                var parent = Require(versions, parentId, "SourceVersion parent");
                if (parent.ConfigId != version.ConfigId || parent.SourceId != version.SourceId)
                {
                    throw Invalid("SourceVersion parent must belong to the same Config and Source.");
                }
            }
        }

        foreach (var representation in representations.Values)
        {
            Require(versions, representation.VersionId, "VersionRepresentation owner");
            foreach (var dependencyId in representation.DependencyRepresentationIds)
            {
                Require(representations, dependencyId, "Representation dependency");
            }
        }

        foreach (var checkpoint in checkpoints.Values)
        {
            RequireConfig(configId, checkpoint.ConfigId, HistoryObjectKinds.ConfigurationCheckpoint, checkpoint.CheckpointId.ToString());
            var duplicateSource = checkpoint.Sources.GroupBy(item => item.SourceId).FirstOrDefault(group => group.Count() > 1);
            if (duplicateSource is not null)
            {
                throw Invalid($"Checkpoint contains duplicate SourceId {duplicateSource.Key}.");
            }

            foreach (var source in checkpoint.Sources.Where(item => item.VersionId is not null))
            {
                var version = Require(versions, source.VersionId!.Value, "Checkpoint SourceVersion");
                if (version.ConfigId != checkpoint.ConfigId || version.SourceId != source.SourceId)
                {
                    throw Invalid("Checkpoint source references a Version for another Config or Source.");
                }
            }
        }

        foreach (var run in runs.Values)
        {
            RequireConfig(configId, run.ConfigId, HistoryObjectKinds.BackupRun, run.RunId.ToString());
            if (run.ResultCheckpointId is { } checkpointId)
            {
                Require(checkpoints, checkpointId, "BackupRun result checkpoint");
            }

            foreach (var source in run.SourceResults.Where(item => item.VersionId is not null))
            {
                var version = Require(versions, source.VersionId!.Value, "BackupRun SourceVersion");
                if (version.SourceId != source.SourceId)
                {
                    throw Invalid("BackupRun source result references another Source identity.");
                }
            }
        }

        foreach (var update in branchUpdates.Values)
        {
            HistoryDomainValidator.ValidateNative(update);
            if (update.TargetCheckpointId is { } targetId)
            {
                Require(checkpoints, targetId, "Branch target checkpoint");
            }

            foreach (var parentId in update.ParentUpdateIds)
            {
                _ = Require(branchUpdates, parentId, "BranchUpdate parent");
            }
        }

        foreach (var replica in replicas.Values)
        {
            Require(representations, replica.RepresentationId, "StorageReplica representation");
            if (!HistoryRepositoryPaths.IsSafeRepositoryRelativePath(replica.ObjectKey))
            {
                throw Invalid($"StorageReplica ObjectKey is unsafe: {replica.ObjectKey}");
            }
        }

        foreach (var update in lifecycles.Values)
        {
            Require(replicas, update.ReplicaId, "ReplicaLifecycle target");
            foreach (var parentId in update.ParentUpdateIds)
            {
                var parent = Require(lifecycles, parentId, "ReplicaLifecycle parent");
                if (parent.ReplicaId != update.ReplicaId)
                {
                    throw Invalid("ReplicaLifecycle parent must target the same ReplicaId.");
                }

                if (parent.State == ReplicaLifecycleState.Retired)
                {
                    throw Invalid("Retired is a terminal Replica lifecycle state.");
                }
            }
        }

        foreach (var update in annotations.Values)
        {
            ValidateAnnotationTarget(update, versions, checkpoints, runs);
            foreach (var parentId in update.ParentUpdateIds)
            {
                var parent = Require(annotations, parentId, "Annotation parent");
                if (parent.Target != update.Target || parent.AnnotationKind != update.AnnotationKind)
                {
                    throw Invalid("Annotation parent must have the same target and kind.");
                }
            }
        }

        foreach (var update in policies.Values)
        {
            Require(versions, update.VersionId, "MaterializationPolicy target");
            foreach (var parentId in update.ParentUpdateIds)
            {
                var parent = Require(policies, parentId, "MaterializationPolicy parent");
                if (parent.VersionId != update.VersionId)
                {
                    throw Invalid("MaterializationPolicy parent must target the same VersionId.");
                }
            }
        }

        foreach (var record in migrationRecords.Values)
        {
            Require(versions, record.VersionId, "LegacyMigrationRecord Version");
        }

        foreach (var snapshot in safetySnapshots.Values)
        {
            var checkpoint = Require(checkpoints, snapshot.CheckpointId, "SafetySnapshot checkpoint");
            if (!checkpoint.IsStructurallyComplete)
                throw Invalid("SafetySnapshot requires a structurally complete checkpoint.");
        }

        foreach (var release in safetySnapshotReleases.Values)
            Require(safetySnapshots, release.SnapshotId, "SafetySnapshotRelease target");

        EnsureAcyclic(versions.Values, item => item.VersionId, item => item.ParentVersionIds, "SourceVersion");
        EnsureAcyclic(representations.Values, item => item.RepresentationId, item => item.DependencyRepresentationIds, "Representation");
        EnsureAcyclic(branchUpdates.Values, item => item.UpdateId, item => item.ParentUpdateIds, "BranchUpdate");
        EnsureAcyclic(lifecycles.Values, item => item.UpdateId, item => item.ParentUpdateIds, "ReplicaLifecycle");
        EnsureAcyclic(annotations.Values, item => item.UpdateId, item => item.ParentUpdateIds, "Annotation");
        EnsureAcyclic(policies.Values, item => item.UpdateId, item => item.ParentUpdateIds, "MaterializationPolicy");
    }

    public static IReadOnlyDictionary<HistoryObjectKey, HistoryPackObject> BuildObjectMap(
        IEnumerable<HistoryPackReadResult> packs)
    {
        var map = new Dictionary<HistoryObjectKey, HistoryPackObject>();
        foreach (var pack in packs)
        {
            foreach (var item in pack.Pack.Objects)
            {
                if (!map.TryAdd(item.Key, item))
                {
                    var existing = map[item.Key];
                    if (existing.SchemaVersion != item.SchemaVersion
                        || !StringComparer.Ordinal.Equals(existing.PayloadHash, item.PayloadHash)
                        || !existing.CanonicalPayload.AsSpan().SequenceEqual(item.CanonicalPayload))
                    {
                        throw new HistoryIntegrityConflictException(
                            $"Immutable object conflict for {item.Kind}/{item.Id}.");
                    }
                }
            }
        }

        return map;
    }

    private static void ValidateAnnotationTarget(
        HistoryAnnotationUpdate update,
        IReadOnlyDictionary<VersionId, SourceVersion> versions,
        IReadOnlyDictionary<CheckpointId, ConfigurationCheckpoint> checkpoints,
        IReadOnlyDictionary<RunId, BackupRun> runs)
    {
        var exists = update.Target.Kind switch
        {
            HistoryAnnotationTargetKind.Version => versions.ContainsKey(new VersionId(update.Target.TargetId)),
            HistoryAnnotationTargetKind.Checkpoint => checkpoints.ContainsKey(new CheckpointId(update.Target.TargetId)),
            HistoryAnnotationTargetKind.Run => runs.ContainsKey(new RunId(update.Target.TargetId)),
            _ => false
        };
        if (!exists)
        {
            throw Invalid("HistoryAnnotation target does not exist or has the wrong kind.");
        }
    }

    private static TValue Require<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> map,
        TKey key,
        string relationship)
        where TKey : notnull
        => map.TryGetValue(key, out var value)
            ? value
            : throw Invalid($"{relationship} '{key}' is missing.");

    private static void RequireConfig(
        HistoryConfigId expected,
        HistoryConfigId actual,
        string kind,
        string id)
    {
        if (expected != actual)
        {
            throw Invalid($"{kind}/{id} belongs to Config '{actual}', expected '{expected}'.");
        }
    }

    private static void EnsureAcyclic<TNode, TId>(
        IEnumerable<TNode> nodes,
        Func<TNode, TId> getId,
        Func<TNode, IEnumerable<TId>> getParents,
        string graphName)
        where TId : notnull
    {
        var map = nodes.ToDictionary(getId);
        var visiting = new HashSet<TId>();
        var visited = new HashSet<TId>();
        foreach (var id in map.Keys)
        {
            Visit(id);
        }

        void Visit(TId id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
            {
                throw Invalid($"{graphName} graph contains a cycle at '{id}'.");
            }

            foreach (var parent in getParents(map[id]))
            {
                Visit(parent);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static HistoryRepositoryValidationException Invalid(string message) => new(message);
}
