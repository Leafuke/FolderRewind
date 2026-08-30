using System;
using System.Linq;

namespace FolderRewind.History.Domain;

public enum SafetySnapshotReason
{
    BeforeCheckout = 0,
    BeforeRestore = 1
}

public sealed record SafetySnapshot
{
    public SafetySnapshot(
        SafetySnapshotId snapshotId,
        CheckpointId checkpointId,
        DateTimeOffset createdAtUtc,
        SafetySnapshotReason reason)
    {
        SnapshotId = snapshotId;
        CheckpointId = checkpointId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Reason = reason;
    }

    public SafetySnapshotId SnapshotId { get; }
    public CheckpointId CheckpointId { get; }
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
