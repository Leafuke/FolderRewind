using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using System.Threading;

namespace FolderRewind.Services;

internal static class HistoryRestoreOutcomePolicy
{
    internal static void ThrowIfCanceledBeforeMutation(HistoryRestoreResult result, CancellationToken token)
    {
        // A requested cancellation must not hide a durable commit or recovery requirement.
        if (result.Status == HistoryRestoreStatus.BlockedBeforeMutation) token.ThrowIfCancellationRequested();
    }

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
