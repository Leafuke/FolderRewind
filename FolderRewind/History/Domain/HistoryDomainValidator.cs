using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.History.Domain;

public sealed class HistoryDomainValidationException(string message) : Exception(message);

public static class HistoryDomainValidator
{
    public static void ValidateNative(SourceVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var maxParents = version.CreationKind == SourceVersionCreationKind.Merge ? 2 : 1;
        if (version.ParentVersionIds.Length > maxParents)
        {
            throw new HistoryDomainValidationException(
                $"{version.CreationKind} SourceVersion creation allows at most {maxParents} semantic parent(s).");
        }
        if (version.ParentVersionIds.Distinct().Count() != version.ParentVersionIds.Length)
        {
            throw new HistoryDomainValidationException("SourceVersion semantic parents cannot contain duplicates.");
        }
        if (version.CaptureScope == CaptureScope.PartialSource
            && version.CreationKind is SourceVersionCreationKind.Capture or SourceVersionCreationKind.Merge
            && version.ParentVersionIds.IsEmpty)
        {
            throw new HistoryDomainValidationException(
                "A PartialSource Version requires a reliable Exact logical parent.");
        }
    }

    public static void ValidateNative(ConfigurationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.CreationKind == CheckpointCreationKind.Merge
            && checkpoint.ParentCheckpointIds.Length != 2)
        {
            throw new HistoryDomainValidationException("A Merge checkpoint requires exactly two semantic parents.");
        }
        if (checkpoint.CreationKind != CheckpointCreationKind.Merge
            && checkpoint.ParentCheckpointIds.Length > 1)
        {
            throw new HistoryDomainValidationException(
                $"{checkpoint.CreationKind} checkpoint creation allows at most one semantic parent.");
        }
        if (checkpoint.ParentCheckpointIds.Distinct().Count() != checkpoint.ParentCheckpointIds.Length)
        {
            throw new HistoryDomainValidationException("Checkpoint semantic parents cannot contain duplicates.");
        }
        if (checkpoint.Sources.GroupBy(item => item.SourceId).Any(group => group.Count() > 1))
        {
            throw new HistoryDomainValidationException("Checkpoint Source roster cannot contain duplicate SourceIds.");
        }
    }

    public static void ValidateNative(BranchUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ParentUpdateIds.Length > 1 && update.Reason != BranchUpdateReason.Reconciled)
        {
            throw new HistoryDomainValidationException(
                "Native BranchUpdate creation is single-parent in the current format stage.");
        }
        if (update.Reason == BranchUpdateReason.Reconciled)
        {
            if (update.ParentUpdateIds.Length < 2)
                throw new HistoryDomainValidationException("A reconciliation update requires at least two parents.");
            var ordered = update.ParentUpdateIds
                .OrderBy(item => item.ToString(), StringComparer.Ordinal)
                .ToArray();
            if (!ordered.SequenceEqual(update.ParentUpdateIds))
                throw new HistoryDomainValidationException("Reconciliation parents must use canonical ordinal order.");
        }
    }

    public static void ValidateAnnotationGraph(IEnumerable<HistoryAnnotationUpdate> updates)
    {
        var map = updates.ToDictionary(update => update.UpdateId);
        foreach (var update in map.Values)
        {
            foreach (var parentId in update.ParentUpdateIds)
            {
                if (!map.TryGetValue(parentId, out var parent))
                {
                    throw new HistoryDomainValidationException($"Missing annotation parent {parentId}.");
                }

                if (parent.Target != update.Target || parent.AnnotationKind != update.AnnotationKind)
                {
                    throw new HistoryDomainValidationException(
                        "Annotation parent must have the same target and annotation kind.");
                }
            }
        }
    }

    public static void ValidateMaterializationPolicyGraph(IEnumerable<MaterializationPolicyUpdate> updates)
    {
        var map = updates.ToDictionary(update => update.UpdateId);
        foreach (var update in map.Values)
        {
            foreach (var parentId in update.ParentUpdateIds)
            {
                if (!map.TryGetValue(parentId, out var parent))
                {
                    throw new HistoryDomainValidationException($"Missing policy parent {parentId}.");
                }

                if (parent.VersionId != update.VersionId)
                {
                    throw new HistoryDomainValidationException(
                        "Materialization policy parent must target the same version.");
                }
            }
        }
    }

    public static void ValidateReplicaLifecycleGraph(IEnumerable<ReplicaLifecycleUpdate> updates)
    {
        var map = updates.ToDictionary(update => update.UpdateId);
        foreach (var update in map.Values)
        {
            foreach (var parentId in update.ParentUpdateIds)
            {
                if (!map.TryGetValue(parentId, out var parent))
                {
                    throw new HistoryDomainValidationException($"Missing lifecycle parent {parentId}.");
                }

                if (parent.ReplicaId != update.ReplicaId)
                {
                    throw new HistoryDomainValidationException(
                        "Replica lifecycle parent must target the same replica.");
                }

                if (parent.State == ReplicaLifecycleState.Retired)
                {
                    throw new HistoryDomainValidationException("A retired replica cannot be revived or updated.");
                }
            }
        }
    }
}
