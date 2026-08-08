using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupRunRestoreOrchestratorTests
{
    [TestMethod]
    public async Task SourceFailureDoesNotPreventLaterRestoreAndUnavailableIsReported()
    {
        var run = new BackupRunRecord
        {
            RunId = "run",
            ConfigId = "config",
            Sources =
            {
                Source("first", BackupRunSourceStatus.NewArchive, "history-1"),
                Source("second", BackupRunSourceStatus.Reused, "history-2"),
                Source("missing", BackupRunSourceStatus.Unavailable, error: "drive offline")
            }
        };
        var attempted = new List<string>();

        var result = await BackupRunRestoreOrchestrator.RestoreAsync(run, source =>
        {
            attempted.Add(source.FolderPath);
            if (source.FolderPath == "first") throw new IOException("archive missing");
            return Task.FromResult(new BackupRunRestoreSourceResult
            {
                FolderPath = source.FolderPath,
                Success = true
            });
        });

        CollectionAssert.AreEqual(new[] { "first", "second" }, attempted);
        Assert.HasCount(3, result.Sources);
        Assert.IsFalse(result.Sources[0].Success);
        StringAssert.Contains(result.Sources[0].ErrorMessage, "archive missing");
        Assert.IsTrue(result.Sources[1].Success);
        Assert.AreEqual("drive offline", result.Sources[2].ErrorMessage);
        Assert.IsFalse(result.Success);
    }

    private static BackupRunSourceRecord Source(
        string path,
        BackupRunSourceStatus status,
        string historyId = "",
        string error = "") => new()
    {
        FolderPath = path,
        Status = status,
        HistoryItemId = historyId,
        ErrorMessage = error
    };
}
