using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryRestoreStatus
{
    Succeeded = 0,
    Blocked = 1,
    Failed = 2,
    RollbackFailed = 3
}

public enum HistoryCheckoutProtectionMode
{
    ProtectCurrentWork = 0,
    DiscardCurrentChanges = 1
}

public enum HistoryCheckpointRestoreScope
{
    CompleteCheckpoint = 0,
    AvailableMappedSources = 1
}

public enum HistoryRestoreApplyMode
{
    Clean = 0,
    Overwrite = 1
}

public sealed record HistoryRestoreSourceBinding(
    SourceId SourceId,
    string TargetDirectory,
    EffectiveSourceBoundarySnapshot? EffectiveSourceBoundary = null)
{
    public EffectiveSourceBoundarySnapshot Boundary =>
        EffectiveSourceBoundary ?? EffectiveSourceBoundarySnapshot.All;
}

public sealed record HistoryRestoreResult(
    HistoryRestoreStatus Status,
    string Diagnostic,
    bool WorkspaceUpdated,
    IReadOnlyList<SourceId> AppliedSources)
{
    public bool Succeeded => Status == HistoryRestoreStatus.Succeeded;
}

public sealed record HistoryRestoreRollbackSnapshot(
    SourceId SourceId,
    string TargetDirectory,
    string RollbackDirectory,
    bool HadOriginalTarget);

public interface IHistoryWorkingStateProtector
{
    /// <summary>Return the current Workspace after any required safety Checkpoint has committed.</summary>
    Task<HistoryWorkspace> ProtectAsync(
        HistoryWorkspace expectedWorkspace,
        CancellationToken cancellationToken);
}

public interface IHistoryRestoreMutationBackend
{
    HistoryRestoreRollbackSnapshot PlanRollback(
        HistoryRestoreSourceBinding source,
        HistoryTransactionId transactionId);

    Task PrepareRollbackAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        HistoryRestoreSourceBinding source,
        string stagingDirectory,
        HistoryRestoreApplyMode applyMode,
        HistoryRestoreRollbackSnapshot rollbackSnapshot,
        CancellationToken cancellationToken);

    Task RollbackAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken);

    Task CommitAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken);
}
