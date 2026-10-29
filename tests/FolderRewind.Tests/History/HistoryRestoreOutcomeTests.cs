using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRestoreOutcomeTests
{
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
