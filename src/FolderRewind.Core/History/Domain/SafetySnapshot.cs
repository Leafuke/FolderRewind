using System;
using System.Linq;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum SafetySnapshotReason
{
    BeforeCheckout = 0,
    BeforeRestore = 1,
    BeforeMerge = 2
}

public sealed record SafetySnapshot
{
    [JsonConstructor]
    public SafetySnapshot(
        SafetySnapshotId snapshotId,
        ImmutableArray<CheckpointId> checkpointIds,
        DateTimeOffset createdAtUtc,
        SafetySnapshotReason reason)
    {
        SnapshotId = snapshotId;
        CheckpointIds = checkpointIds.IsDefault ? [] : checkpointIds;
        if (CheckpointIds.Length == 0 || CheckpointIds.Distinct().Count() != CheckpointIds.Length)
            throw new ArgumentException("Safety snapshot requires distinct Source checkpoints.");
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Reason = reason;
    }

    public SafetySnapshotId SnapshotId { get; }
    public ImmutableArray<CheckpointId> CheckpointIds { get; }

    public SafetySnapshot(SafetySnapshotId snapshotId, IEnumerable<CheckpointId> checkpointIds, DateTimeOffset createdAtUtc, SafetySnapshotReason reason)
        : this(snapshotId, [.. checkpointIds], createdAtUtc, reason) { }

    public SafetySnapshot(SafetySnapshotId snapshotId, CheckpointId checkpointId, DateTimeOffset createdAtUtc, SafetySnapshotReason reason)
        : this(snapshotId, [checkpointId], createdAtUtc, reason) { }
    public DateTimeOffset CreatedAtUtc { get; }
    public SafetySnapshotReason Reason { get; }
}

public sealed record SafetySnapshotRelease
{
    public SafetySnapshotRelease(
        SafetySnapshotReleaseId releaseId,
        SafetySnapshotId snapshotId,
        DateTimeOffset releasedAtUtc)
    {
        ReleaseId = releaseId;
        SnapshotId = snapshotId;
        ReleasedAtUtc = releasedAtUtc.ToUniversalTime();
    }

    public SafetySnapshotReleaseId ReleaseId { get; }
    public SafetySnapshotId SnapshotId { get; }
    public DateTimeOffset ReleasedAtUtc { get; }
}

public sealed record SafetySnapshotProjection(
    SafetySnapshot Snapshot,
    bool IsActive,
    int ReleaseFactCount);

public static class SafetySnapshotProjectionService
{
    public static SafetySnapshotProjection Project(
        SafetySnapshot snapshot,
        System.Collections.Generic.IEnumerable<SafetySnapshotRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var count = releases?.Count(item => item.SnapshotId == snapshot.SnapshotId) ?? 0;
        return new SafetySnapshotProjection(snapshot, count == 0, count);
    }
}
