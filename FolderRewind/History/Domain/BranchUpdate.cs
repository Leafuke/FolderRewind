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
    Migration = 5,
    Reconciled = 6,
    Merged = 7
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
        BranchUpdateReason reason,
        BranchMergeProvenance? mergeProvenance = null)
        : this(
            updateId,
            branchId,
            DomainCollections.Freeze(parentUpdateIds),
            name,
            targetCheckpointId,
            isDeleted,
            createdAtUtc,
            reason, mergeProvenance)
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
        BranchUpdateReason reason,
        BranchMergeProvenance? mergeProvenance = null)
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
        MergeProvenance = mergeProvenance;
    }

    public BranchUpdateId UpdateId { get; }
    public BranchId BranchId { get; }
    public ImmutableArray<BranchUpdateId> ParentUpdateIds { get; }
    public string Name { get; }
    public CheckpointId? TargetCheckpointId { get; }
    public bool IsDeleted { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public BranchUpdateReason Reason { get; }
    public BranchMergeProvenance? MergeProvenance { get; }
    public bool IsUnborn => !IsDeleted && TargetCheckpointId is null;
}

public enum BranchMergeMode { FastForwardLike, ThreeWay }
public sealed record BranchMergeProvenance(BranchMergeMode Mode, BranchId TargetBranchId, BranchId SourceBranchId,
    BranchUpdateId OursUpdateId, BranchUpdateId TheirsUpdateId, CheckpointId OursCheckpointId,
    CheckpointId TheirsCheckpointId, CheckpointId? BaseCheckpointId, string ProviderVersion,
    string PolicyVersion, string ResolutionDigest);
