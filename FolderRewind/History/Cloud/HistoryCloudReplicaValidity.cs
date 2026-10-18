using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.History.Cloud;

public static class HistoryCloudReplicaValidity
{
    public static bool IsActive(IEnumerable<ReplicaLifecycleUpdate> updates)
    {
        var all = updates.ToArray(); var parents = all.SelectMany(u => u.ParentUpdateIds).ToHashSet();
        var tips = all.Where(u => !parents.Contains(u.UpdateId)).ToArray();
        return tips.Length > 0 && tips.All(u => u.State == ReplicaLifecycleState.Active);
    }
    public static bool Matches(HistoryReplicaManifest actual, HistoryReplicaManifest expected)
        => actual.RepresentationId == expected.RepresentationId && actual.ReplicaId == expected.ReplicaId
            && actual.ObjectKey == expected.ObjectKey && actual.Size == expected.Size
            && StringComparer.OrdinalIgnoreCase.Equals(actual.StorageSha256, expected.StorageSha256)
            && actual.ArtifactRootId == expected.ArtifactRootId;
    public static Guid? ArtifactRoot(VersionRepresentation representation)
        => representation.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var value)
            && Guid.TryParse(value, out var id) ? id : null;
}
