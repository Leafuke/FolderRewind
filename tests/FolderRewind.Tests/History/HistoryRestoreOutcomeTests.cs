using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRestoreOutcomeTests
{
    [TestMethod]
    public void CancellationBeforeMutationKeepsItsCancellationOutcome()
    {
        var result = new HistoryRestoreResult(HistoryRestoreStatus.BlockedBeforeMutation, "A task was canceled.", false, []);
        HistoryRestoreOutcomePolicy.ThrowIfCanceledBeforeMutation(result, default);
        Assert.ThrowsExactly<OperationCanceledException>(() => HistoryRestoreOutcomePolicy.ThrowIfCanceledBeforeMutation(result, new(true)));
    }

    [TestMethod]
    [DataRow(HistoryRestoreStatus.Committed)]
    [DataRow(HistoryRestoreStatus.CommittedWithPostActionWarning)]
    [DataRow(HistoryRestoreStatus.CommittedRecoveryRequired)]
    [DataRow(HistoryRestoreStatus.MutationFailedRecoveryRequired)]
    [DataRow(HistoryRestoreStatus.MutationFailedRolledBack)]
    [DataRow(HistoryRestoreStatus.NoChanges)]
    [DataRow(HistoryRestoreStatus.PreparationRequired)]
    public void LateCancellationDoesNotReplaceTheFileOrRecoveryOutcome(HistoryRestoreStatus status)
    {
        var result = new HistoryRestoreResult(status, "retain this outcome", false, []);
        HistoryRestoreOutcomePolicy.ThrowIfCanceledBeforeMutation(result, new(true));
        Assert.AreEqual(status, result.Status);
    }

    [TestMethod]
    [DataRow(HistoryRestoreStatus.NoChanges, false, OperationOutcome.SuccessWithWarnings)]
    [DataRow(HistoryRestoreStatus.Committed, true, OperationOutcome.SuccessWithWarnings)]
    [DataRow(HistoryRestoreStatus.MutationFailedRolledBack, false, OperationOutcome.Failed)]
    [DataRow(HistoryRestoreStatus.CommittedRecoveryRequired, true, OperationOutcome.CommittedRecoveryRequired)]
    [DataRow(HistoryRestoreStatus.MutationFailedRecoveryRequired, false, OperationOutcome.RecoveryRequired)]
    public void ReentryFailureRetainsFileOutcomeAndRecoveryRequirements(HistoryRestoreStatus status, bool committed, OperationOutcome requestOutcome)
    {
        var original = new HistoryRestoreResult(status, "file result", committed, []);
        var postAction = original.WithPostActionWarning("reentry timeout");
        Assert.AreEqual(committed, postAction.TargetCommitted);
        Assert.AreEqual(original.Succeeded, postAction.Succeeded);
        Assert.AreEqual(requestOutcome, HistoryRestoreOutcomePolicy.ForRequest(postAction));
        if (status != HistoryRestoreStatus.Committed) Assert.AreEqual(status, postAction.Status);
        StringAssert.Contains(postAction.Diagnostic, "file result");
        StringAssert.Contains(postAction.PostActionDiagnostic!, "reentry timeout");
    }
}
