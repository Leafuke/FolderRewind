using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using System.Linq;

namespace FolderRewind.Services;

internal static class NativeRestoreCoordinationPolicy
{
    internal static HistoryRestoreResult Resolve(bool wasInvoked, HistoryRestoreResult? mutation, RestoreCoordinatorResult coordinated)
    {
        if (!wasInvoked)
        {
            var expectedStop = coordinated.Outcome is OperationOutcome.Blocked or OperationOutcome.Failed or OperationOutcome.Canceled;
            var reason = !expectedStop ? I18n.GetString("Restore_CoordinatorContinuationMissing")
                : coordinated.Diagnostics.Count > 0 ? OperationDiagnosticFormatter.Summary(coordinated.Diagnostics)
                : I18n.GetString(coordinated.Outcome switch
                {
                    OperationOutcome.Canceled => "Restore_CoordinatorCanceled",
                    OperationOutcome.Failed => "Restore_CoordinatorFailed",
                    _ => "Restore_CoordinatorBlocked"
                });
            return new(HistoryRestoreStatus.BlockedBeforeMutation, reason, false, []);
        }
        if (mutation is null)
            return new(HistoryRestoreStatus.BlockedBeforeMutation, I18n.GetString("Restore_CoordinatorContinuationFailed"), false, []);
        if (mutation.Status is HistoryRestoreStatus.CommittedRecoveryRequired or HistoryRestoreStatus.MutationFailedRecoveryRequired)
            return mutation;
        var warned = coordinated.Outcome is not (OperationOutcome.Success or OperationOutcome.NoChanges)
            || coordinated.Diagnostics.Any(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error);
        if (!warned) return mutation;
        return mutation.WithPostActionWarning(I18n.Format("Restore_CoordinatorPostActionFailed", coordinated.Diagnostics.Count == 0
            ? I18n.GetString("Restore_CoordinatorPostActionWarning") : OperationDiagnosticFormatter.Summary(coordinated.Diagnostics)));
    }
}
