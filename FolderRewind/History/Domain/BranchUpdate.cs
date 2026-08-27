using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum BranchUpdateReason
{
    Created = 0,
    Backup = 1,
    BackupFromHistoricalState = 2,
    Renamed = 3,
    Deleted = 4,
    Migration = 5
}

public sealed record BranchUpdate
{
    public BranchUpdate(
        BranchUpdateId updateId,
        BranchId branchId,
        IEnumerable<BranchUpdateId>? parentUpdateIds,
        string name,
        CheckpointId? targetCheckpointId,
        bool isDeleted,
        DateTimeOffset createdAtUtc,
        BranchUpdateReason reason)
        : this(
            updateId,
            branchId,
            DomainCollections.Freeze(parentUpdateIds),
            name,
            targetCheckpointId,
            isDeleted,
            createdAtUtc,
            reason)
    {
    }

    [JsonConstructor]
    public BranchUpdate(
        BranchUpdateId updateId,
        BranchId branchId,
        ImmutableArray<BranchUpdateId> parentUpdateIds,
        string name,
        CheckpointId? targetCheckpointId,
        bool isDeleted,
        DateTimeOffset createdAtUtc,
        BranchUpdateReason reason)
    {
        UpdateId = updateId;
        BranchId = branchId;
        ParentUpdateIds = parentUpdateIds.IsDefault ? ImmutableArray<BranchUpdateId>.Empty : parentUpdateIds;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Branch name cannot be empty.", nameof(name))
            : name.Trim();
        TargetCheckpointId = targetCheckpointId;
        IsDeleted = isDeleted;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Reason = reason;
    }

    public BranchUpdateId UpdateId { get; }
    public BranchId BranchId { get; }
    public ImmutableArray<BranchUpdateId> ParentUpdateIds { get; }
    public string Name { get; }
    public CheckpointId? TargetCheckpointId { get; }
    public bool IsDeleted { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public BranchUpdateReason Reason { get; }
    public bool IsUnborn => !IsDeleted && TargetCheckpointId is null;
}
