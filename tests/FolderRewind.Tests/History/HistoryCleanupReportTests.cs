using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryCleanupReportTests
{
    [TestMethod]
    [DataRow("Completed", false)]
    [DataRow("NoWork", false)]
    [DataRow("Incomplete", true)]
    [DataRow("Partial", true)]
    [DataRow("Canceled", true)]
    [DataRow("Interrupted", true)]
    public void CleanupStatusDeterminesPostCommitWarning(string status, bool warning)
        => Assert.AreEqual(warning, new HistoryCleanupReport { Status = status }.HasWarnings);

    [TestMethod]
    public void CompletedCleanupStillWarnsWhenReportOrSourceHasFailures()
    {
        var report = new HistoryCleanupReport { Status = "Completed" };
        report.Issues.Add(new("ReportWriteFailed", "disk unavailable"));
        Assert.IsTrue(report.HasWarnings);
        report.Issues.Clear();
        report.Sources.Add(new() { Status = "Completed", Issues = [new("AssessmentFailed")] });
        Assert.IsTrue(report.HasWarnings);
    }
}
