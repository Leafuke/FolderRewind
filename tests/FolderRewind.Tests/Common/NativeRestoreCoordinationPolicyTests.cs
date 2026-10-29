using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeRestoreCoordinationPolicyTests
{
    [TestMethod]
    [DataRow(OperationOutcome.Blocked)]
    [DataRow(OperationOutcome.Failed)]
    [DataRow(OperationOutcome.Canceled)]
    public void ExpectedPreMutationStopsPreserveTheReportedReason(OperationOutcome outcome)
    {
        var diagnostic = new PluginDiagnostic("thirdparty.world_busy", DiagnosticSeverity.Error, "RestoreCoordinator",
            "test.plugin", new Dictionary<string, string> { ["path"] = "world" });
        var result = NativeRestoreCoordinationPolicy.Resolve(false, null, new(outcome, [diagnostic]));
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status);
        StringAssert.Contains(result.Diagnostic, "thirdparty.world_busy");
        StringAssert.Contains(result.Diagnostic, "test.plugin");
        Assert.IsFalse(result.WorkspaceUpdated);
        Assert.IsEmpty(result.AppliedSources);
    }

    [TestMethod]
    [DataRow(OperationOutcome.Success)]
    [DataRow(OperationOutcome.SuccessWithWarnings)]
    [DataRow(OperationOutcome.NoChanges)]
    public void ClaimedCompletionRequiresTheHostContinuation(OperationOutcome outcome)
        => Assert.AreEqual("Restore_CoordinatorContinuationMissing",
            NativeRestoreCoordinationPolicy.Resolve(false, null, new(outcome, [])).Diagnostic);

    [TestMethod]
    public void InvokedContinuationWithoutAResultIsNotReportedAsNeverCalled()
        => Assert.AreEqual("Restore_CoordinatorContinuationFailed",
            NativeRestoreCoordinationPolicy.Resolve(true, null, new(OperationOutcome.Failed, [])).Diagnostic);

    [TestMethod]
    [DataRow(HistoryRestoreStatus.CommittedRecoveryRequired)]
    [DataRow(HistoryRestoreStatus.MutationFailedRecoveryRequired)]
    [DataRow(HistoryRestoreStatus.MutationFailedRolledBack)]
    public void CoordinatorFailureDoesNotOverwriteTheHostMutationStatus(HistoryRestoreStatus status)
    {
        var host = new HistoryRestoreResult(status, "original mutation evidence", false, []);
        var result = NativeRestoreCoordinationPolicy.Resolve(true, host, new(OperationOutcome.Failed, []));
        Assert.AreEqual(status, result.Status);
        StringAssert.Contains(result.Diagnostic, "original mutation evidence");
        if (status is HistoryRestoreStatus.CommittedRecoveryRequired or HistoryRestoreStatus.MutationFailedRecoveryRequired)
            Assert.AreSame(host, result);
    }

    [TestMethod]
    public void PostActionWarningPreservesNoChangeAndSuccessfulMutation()
    {
        foreach (var status in new[] { HistoryRestoreStatus.NoChanges, HistoryRestoreStatus.Committed })
        {
            var host = new HistoryRestoreResult(status, "", true, []);
            var diagnostic = new PluginDiagnostic("minerewind.restore_rejoin_failed", DiagnosticSeverity.Warning,
                "RestoreCoordinator", "com.folderrewind.minerewind", new Dictionary<string, string>());
            var result = NativeRestoreCoordinationPolicy.Resolve(true, host, new(OperationOutcome.SuccessWithWarnings, [diagnostic]));
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(status == HistoryRestoreStatus.Committed ? HistoryRestoreStatus.CommittedWithPostActionWarning : status, result.Status);
            Assert.IsNotNull(result.PostActionDiagnostic);
        }
    }
}
