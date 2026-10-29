using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SetupBackupPresentationPolicyTests
{
    private static SetupSourceResult Captured(string id) => new(id, id, SetupSourceOutcome.Captured, "version-" + id, false, []);
    private static SetupBackupResult Result(params SetupSourceResult[] sources) => new(sources, null, "run", false, DateTimeOffset.UtcNow);

    [TestMethod]
    public void ACommittedSmallArchiveWarningIsVisibleWithoutChangingTheSuccessfulOutcome()
    {
        var source = Captured("a") with { WarningDiagnostics = [new PluginDiagnostic("backup.file_too_small", DiagnosticSeverity.Warning, "Backup", "folderrewind.core", new Dictionary<string, string>())] };
        var result = Result(source);
        Assert.IsTrue(result.HasWarnings);
        Assert.AreEqual(1, result.CreatedVersionCount);
        Assert.AreEqual(SetupBackupSummaryState.CompleteWithWarnings, SetupBackupPresentationPolicy.Summarize(true, [result], 0));
        Assert.AreEqual(SetupBackupSummaryState.Complete, SetupBackupPresentationPolicy.Summarize(true, [Result(Captured("b") with { IsPartial = true })], 0));
    }

    [TestMethod]
    public void RetryingOneSourceRetainsOtherWarningsAndReplacesItsOwnFailure()
    {
        var good = Captured("a") with { Diagnostics = ["original warning"] };
        var failed = new SetupSourceResult("b", "b", SetupSourceOutcome.Failed, null, false, ["old failure"]);
        var canceled = new SetupSourceResult("c", "c", SetupSourceOutcome.Canceled, null, false, []);
        var original = Result(good, failed, canceled);
        var retried = original.MergeRetry(Result(Captured("b")));
        Assert.AreEqual(SetupBackupSummaryState.Incomplete, SetupBackupPresentationPolicy.Summarize(true, [retried], 0));
        Assert.HasCount(3, retried.Sources);
        Assert.IsEmpty(retried.Sources.Single(s => s.SourceId == "b").Diagnostics);
        retried = retried.MergeRetry(Result(Captured("c")));
        Assert.AreEqual(SetupBackupSummaryState.CompleteWithWarnings, SetupBackupPresentationPolicy.Summarize(true, [retried], 0));
        CollectionAssert.AreEqual(new[] { "original warning" }, retried.Sources.Single(s => s.SourceId == "a").Diagnostics.ToArray());
    }

    [TestMethod]
    public void FailureCancellationRecoveryAndNoChangeHaveDistinctSummaries()
    {
        Assert.AreEqual(SetupBackupSummaryState.Created, SetupBackupPresentationPolicy.Summarize(false, [], 0));
        Assert.AreEqual(SetupBackupSummaryState.Failed, SetupBackupPresentationPolicy.Summarize(true, [], 1));
        var canceled = new SetupSourceResult("a", "a", SetupSourceOutcome.Canceled, null, false, []);
        Assert.AreEqual(SetupBackupSummaryState.Canceled, SetupBackupPresentationPolicy.Summarize(true, [Result(canceled)], 0));
        Assert.AreEqual(SetupBackupSummaryState.Incomplete, SetupBackupPresentationPolicy.Summarize(true, [Result(Captured("a"))], 1));
        Assert.AreEqual(SetupBackupSummaryState.Unchanged, SetupBackupPresentationPolicy.Summarize(true, [Result(Captured("a") with { Outcome = SetupSourceOutcome.NoChange })], 0));
        var recovery = Result(Captured("a")) with { RecoveryRequired = true };
        Assert.AreEqual(SetupBackupSummaryState.NeedsRecovery, SetupBackupPresentationPolicy.Summarize(true, [recovery], 0));
        Assert.IsFalse(recovery.MergeRetry(Result(Captured("a"))).RecoveryRequired);
    }
}
