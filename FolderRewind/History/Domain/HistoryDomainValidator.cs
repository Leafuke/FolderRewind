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
        if (version.ParentVersionIds.Length > 1)
        {
            throw new HistoryDomainValidationException(
                "Native SourceVersion creation is single-parent in the current format stage.");
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
