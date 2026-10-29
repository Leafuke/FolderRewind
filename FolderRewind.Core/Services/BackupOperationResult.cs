using FolderRewind.Plugin.Abstractions;
using System.Collections.Generic;

namespace FolderRewind.Services;

/// <summary>Application-facing projection of the committed backup transaction result.</summary>
public sealed record BackupOperationResult(
    OperationOutcome Outcome,
    bool CreatedNewArchive,
    bool HistoryRecoveryRequired = false)
{
    public bool HasWarnings => HistoryRecoveryRequired || Outcome == OperationOutcome.SuccessWithWarnings;

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

    // Preserve the legacy result field while exposing the committed warning outcome.
    public Dictionary<string, string?> ToProtocolFields() => new()
    {
        ["result"] = ProtocolResult,
        ["outcome"] = Outcome == OperationOutcome.SuccessWithWarnings ? "success_with_warnings" : Outcome.ToString().ToLowerInvariant(),
        ["has_warnings"] = HasWarnings ? "true" : "false",
        ["created"] = CreatedNewArchive ? "true" : "false"
    };
}
