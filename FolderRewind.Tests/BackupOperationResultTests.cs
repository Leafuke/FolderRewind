using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupOperationResultTests
{
    [TestMethod]
    public void UnsuccessfulAttemptsCannotCountAsNoChangeOrCompleteRemoteCommand()
    {
        foreach (var outcome in new[] { OperationOutcome.Failed, OperationOutcome.Blocked,
                     OperationOutcome.Canceled, OperationOutcome.RecoveryRequired, OperationOutcome.CommittedRecoveryRequired })
        {
            foreach (var created in new[] { false, true })
            {
                var result = new BackupOperationResult(outcome, created);
                var count = 2;
                for (var attempt = 0; attempt < 5; attempt++) count = result.NextNoChangeCount(count);
                Assert.AreEqual(2, count);
                Assert.IsFalse(result.CanStopForNoChanges);
                Assert.IsFalse(result.IsSuccessful);
                Assert.AreNotEqual("no_changes", result.ProtocolResult);
                Assert.AreNotEqual("created", result.ProtocolResult);
            }
        }
    }

    [TestMethod]
    public void ConfirmedNoChangeReachesThresholdAndSuccessfulArchiveResetsCount()
    {
        var noChange = new BackupOperationResult(OperationOutcome.NoChanges, false);
        var count = 0;
        for (var i = 0; i < 3; i++) count = noChange.NextNoChangeCount(count);
        Assert.AreEqual(3, count);
        Assert.IsTrue(noChange.CanStopForNoChanges);
        Assert.AreEqual("no_changes", noChange.ProtocolResult);
        foreach (var outcome in new[] { OperationOutcome.Success, OperationOutcome.SuccessWithWarnings })
        {
            var created = new BackupOperationResult(outcome, true);
            Assert.AreEqual(0, created.NextNoChangeCount(count));
            Assert.AreEqual("created", created.ProtocolResult);
        }
    }

    [TestMethod]
    public void RecoveryAndUncertainNoChangePreserveAutomationState()
    {
        var recovery = new BackupOperationResult(OperationOutcome.SuccessWithWarnings, true, true);
        Assert.AreEqual(2, recovery.NextNoChangeCount(2));
        Assert.IsFalse(recovery.CanStopForNoChanges);
        Assert.IsFalse(recovery.IsSuccessful);
        Assert.AreEqual("recovery_required", recovery.ProtocolResult);
        var warning = new BackupOperationResult(OperationOutcome.SuccessWithWarnings, false);
        Assert.AreEqual(2, warning.NextNoChangeCount(2));
        Assert.IsFalse(warning.CanStopForNoChanges);
    }
}
