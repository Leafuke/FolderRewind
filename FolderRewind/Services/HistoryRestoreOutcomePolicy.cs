using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services;

internal static class HistoryRestoreOutcomePolicy
{
    internal static OperationOutcome ForRequest(HistoryRestoreResult result) => result.Status switch
    {
        HistoryRestoreStatus.CommittedRecoveryRequired => OperationOutcome.CommittedRecoveryRequired,
        HistoryRestoreStatus.MutationFailedRecoveryRequired => OperationOutcome.RecoveryRequired,
        HistoryRestoreStatus.NoChanges when result.PostActionDiagnostic is null => OperationOutcome.NoChanges,
        HistoryRestoreStatus.CommittedWithPostActionWarning => OperationOutcome.SuccessWithWarnings,
        _ when result.Succeeded && result.PostActionDiagnostic is not null => OperationOutcome.SuccessWithWarnings,
        _ when result.Succeeded => OperationOutcome.Success,
        HistoryRestoreStatus.BlockedBeforeMutation => OperationOutcome.Blocked,
        _ => OperationOutcome.Failed
    };
}
