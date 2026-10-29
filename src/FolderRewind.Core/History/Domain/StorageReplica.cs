using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum ReplicaProviderKind
{
    Cloud = 0,
    External = 1,
    LegacyCloud = 2
}

public sealed record StorageReplica(
    ReplicaId ReplicaId,
    RepresentationId RepresentationId,
    ReplicaProviderKind ProviderKind,
    string ObjectKey,
    long? ExpectedSize,
    string? ExpectedStorageSha256,
    HistoryProvenance Origin);

public enum ReplicaLifecycleState
{
    Active = 0,
    Retired = 1
}

public sealed record ReplicaLifecycleUpdate
{
    public ReplicaLifecycleUpdate(
        ReplicaLifecycleUpdateId updateId,
        ReplicaId replicaId,
        IEnumerable<ReplicaLifecycleUpdateId>? parentUpdateIds,
        ReplicaLifecycleState state,
        DateTimeOffset createdAtUtc,
        string reason)
        : this(
            updateId,
            replicaId,
            DomainCollections.Freeze(parentUpdateIds),
            state,
            createdAtUtc,
            reason)
    {
    }

    [JsonConstructor]
    public ReplicaLifecycleUpdate(
        ReplicaLifecycleUpdateId updateId,
        ReplicaId replicaId,
        ImmutableArray<ReplicaLifecycleUpdateId> parentUpdateIds,
        ReplicaLifecycleState state,
        DateTimeOffset createdAtUtc,
        string reason)
    {
        UpdateId = updateId;
        ReplicaId = replicaId;
        ParentUpdateIds = parentUpdateIds.IsDefault
            ? ImmutableArray<ReplicaLifecycleUpdateId>.Empty
            : parentUpdateIds;
        State = state;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Reason = reason?.Trim() ?? string.Empty;
    }

    public ReplicaLifecycleUpdateId UpdateId { get; }
    public ReplicaId ReplicaId { get; }
    public ImmutableArray<ReplicaLifecycleUpdateId> ParentUpdateIds { get; }
    public ReplicaLifecycleState State { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string Reason { get; }
}
