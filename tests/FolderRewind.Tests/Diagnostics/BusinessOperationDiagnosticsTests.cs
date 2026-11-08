using FolderRewind.Models;
using FolderRewind.Services;
using System.Diagnostics;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BusinessOperationDiagnosticsTests
{
    [TestMethod]
    public void FinalResultIsUniqueAndCoreSummaryIsDeliveredAfterCompletion()
    {
        var records = new List<(string Text, LogLevel Level)>();
        var operation = new BusinessOperationDiagnostics("Backup", publish: (text, level) => records.Add((text, level)));
        using (var core = HistoryOperationDiagnostics.Begin("capture"))
        { core.Count("files", 3); using var phase = core.Measure("capture"); }
        Assert.HasCount(1, records);
        operation.Complete("CommittedRecoveryRequired", LogLevel.Error, "historyCommitted=true");
        operation.Complete("Success"); // A later presentation callback cannot overwrite the authoritative result.
        operation.Dispose(); operation.Dispose();
        var final = records.Single(r => r.Text.Contains("event=operation.completed;", StringComparison.Ordinal));
        StringAssert.Contains(final.Text, "outcome=CommittedRecoveryRequired");
        StringAssert.Contains(final.Text, "historyCommitted=true");
        Assert.AreEqual(LogLevel.Error, final.Level);
        Assert.IsTrue(records.Any(r => r.Text.Contains("files=3", StringComparison.Ordinal) && r.Level == LogLevel.Error));
        Assert.IsNull(HistoryOperationDiagnostics.Current);
    }

    [TestMethod]
    public void InternalSnapshotDoesNotPublishAnotherParentCompletion()
    {
        var records = new List<string>();
        using (var operation = new BusinessOperationDiagnostics("SafetySnapshot", true, (text, _) => records.Add(text))) operation.Complete("Success");
        Assert.IsFalse(records.Any(r => r.Contains("event=operation.completed;", StringComparison.Ordinal)));
        Assert.HasCount(1, records.Where(r => r.Contains("event=operation.subresult;", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ThrowingDiagnosticSinkDoesNotAffectScopeRestoration()
    {
        using (var operation = new BusinessOperationDiagnostics("Restore", publish: (_, _) => throw new IOException("sink failure"))) operation.Complete("Committed");
        Assert.IsNull(HistoryOperationDiagnostics.Current);
    }

    [TestMethod]
    public void CommandProjectionOmitsArgumentsAndSeparatesTimeoutFromUserCancellation()
    {
        using var operation = new NotificationOperationScope(new(NotificationTargetKind.CloudProject, "job"), context: new("job", "Cloud", JobId: "job", JobAttempt: 2));
        var start = new ProcessStartInfo("rclone"); start.ArgumentList.Add("copy"); start.ArgumentList.Add("bare-fixture-secret");
        var records = new List<(string Text, LogLevel Level, LogOperationContext Context)>();
        void Publish(string text, LogLevel level) => records.Add((text, level, NotificationOperationScope.Current!.Capture()));
        using (var command = new ExternalCommandDiagnostics(start, default, Publish)) command.Complete(null, true, true);
        using var cancellation = new CancellationTokenSource();
        using (var command = new ExternalCommandDiagnostics(start, cancellation.Token, Publish))
        { cancellation.Cancel(); command.Complete(null, true, true); }
        using (var command = new ExternalCommandDiagnostics(start, default, Publish)) command.Complete(12, false, false);
        StringAssert.Contains(records[0].Text, "outcome=TimedOut");
        StringAssert.Contains(records[1].Text, "outcome=Canceled");
        StringAssert.Contains(records[2].Text, "exitCode=12");
        Assert.AreEqual(LogLevel.Info, records[1].Level);
        CollectionAssert.AreEqual(new int?[] { 1, 2, 3 }, records.Select(r => r.Context.CommandAttempt).ToArray());
        Assert.IsTrue(records.All(r => r.Context.OperationId == "job" && r.Context.JobAttempt == 2));
        Assert.IsFalse(records.Any(r => r.Text.Contains("bare-fixture-secret", StringComparison.Ordinal)));
        Assert.AreSame(operation, NotificationOperationScope.Current);
    }

    [TestMethod]
    public void RecoveryGapAndMaintenanceHaveDifferentUiStates()
    {
        var healthy = new LogHealthSnapshot(LogLifecycleState.Running, LogWriterState.Healthy, null, null, null, 0, 0, 0, false);
        Assert.IsNull(LogPresentationController.HealthResourceKey(healthy));
        Assert.AreEqual("LogPersistence_Gap", LogPresentationController.HealthResourceKey(healthy with { HasPersistenceGap = true }));
        Assert.AreEqual("LogPersistence_Maintenance", LogPresentationController.HealthResourceKey(healthy with { LastMaintenanceFailure = "rotation" }));
        Assert.AreEqual("LogPersistence_Rejected", LogPresentationController.HealthResourceKey(healthy with { RejectedAfterStopCount = 1 }));
    }
}
