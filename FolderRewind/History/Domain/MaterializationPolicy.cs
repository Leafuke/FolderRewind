using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum MaterializationPolicyState
{
    Retained = 0,
    Released = 1
}

public sealed record MaterializationPolicyUpdate
{
    public MaterializationPolicyUpdate(
        MaterializationPolicyUpdateId updateId,
        VersionId versionId,
        IEnumerable<MaterializationPolicyUpdateId>? parentUpdateIds,
        MaterializationPolicyState state,
        DateTimeOffset createdAtUtc,
        string reason)
        : this(
            updateId,
            versionId,
            DomainCollections.Freeze(parentUpdateIds),
            state,
            createdAtUtc,
            reason)
    {
    }

    [JsonConstructor]
    public MaterializationPolicyUpdate(
        MaterializationPolicyUpdateId updateId,
        VersionId versionId,
        ImmutableArray<MaterializationPolicyUpdateId> parentUpdateIds,
        MaterializationPolicyState state,
        DateTimeOffset createdAtUtc,
        string reason)
    {
        UpdateId = updateId;
        VersionId = versionId;
        ParentUpdateIds = parentUpdateIds.IsDefault
            ? ImmutableArray<MaterializationPolicyUpdateId>.Empty
            : parentUpdateIds;
        State = state;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Reason = reason?.Trim() ?? string.Empty;
    }

    public MaterializationPolicyUpdateId UpdateId { get; }
    public VersionId VersionId { get; }
    public ImmutableArray<MaterializationPolicyUpdateId> ParentUpdateIds { get; }
    public MaterializationPolicyState State { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string Reason { get; }
}
