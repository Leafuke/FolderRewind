using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services;

/// <summary>Application-facing projection of the committed backup transaction result.</summary>
public sealed record BackupOperationResult(
    OperationOutcome Outcome,
    bool CreatedNewArchive,
    bool HistoryRecoveryRequired = false)
{
    public bool IsSuccessful => !HistoryRecoveryRequired
        && Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings or OperationOutcome.NoChanges;

    // Warnings can include uncertain source state: never count them as confirmed no-change.
    public int NextNoChangeCount(int current) => !HistoryRecoveryRequired
        && Outcome is OperationOutcome.Success or OperationOutcome.NoChanges
            ? CreatedNewArchive ? 0 : current + 1
            : IsSuccessful && CreatedNewArchive ? 0 : current;

    public bool CanStopForNoChanges => !HistoryRecoveryRequired && !CreatedNewArchive
        && Outcome is OperationOutcome.Success or OperationOutcome.NoChanges;

    public string ProtocolResult => IsSuccessful
        ? CreatedNewArchive ? "created" : "no_changes"
        : HistoryRecoveryRequired ? "recovery_required" : Outcome.ToString().ToLowerInvariant();
}
