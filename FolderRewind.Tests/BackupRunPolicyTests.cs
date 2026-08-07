using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupRunPolicyTests
{
    [TestMethod]
    public void LegacyHistoryIdentityIsDeterministicAndSourceSpecific()
    {
        var timestamp = new DateTime(2026, 8, 7, 8, 0, 0, DateTimeKind.Utc);

        var first = HistoryItemIdentity.CreateLegacyId("config", "C:\\Game\\Saves", "backup.7z", timestamp);
        var same = HistoryItemIdentity.CreateLegacyId("CONFIG", "c:/game/saves", "BACKUP.7Z", timestamp);
        var otherSource = HistoryItemIdentity.CreateLegacyId("config", "C:\\Game\\Config", "backup.7z", timestamp);

        Assert.AreEqual(first, same);
        Assert.AreNotEqual(first, otherSource);
    }

    [TestMethod]
    public void AllUnchangedOrAllFailedDoesNotCreateEmptyRun()
    {
        var unchanged = BackupRunPolicy.Create(
            "run",
            "config",
            DateTime.UtcNow,
            DateTime.UtcNow,
            BackupRunTriggerSource.Manual,
            string.Empty,
            new[]
            {
                Source(BackupRunSourceStatus.Reused, "history-1"),
                Source(BackupRunSourceStatus.Reused, "history-2")
            });
        var failed = BackupRunPolicy.Create(
            "run",
            "config",
            DateTime.UtcNow,
            DateTime.UtcNow,
            BackupRunTriggerSource.Manual,
            string.Empty,
            new[] { Source(BackupRunSourceStatus.Failed) });

        Assert.IsNull(unchanged);
        Assert.IsNull(failed);
    }

    [TestMethod]
    public void NewArchiveWithFailureCreatesPartialRunAndKeepsReuseReference()
    {
        var run = BackupRunPolicy.Create(
            "run",
            "config",
            DateTime.UtcNow,
            DateTime.UtcNow,
            BackupRunTriggerSource.Automatic,
            "scheduled",
            new[]
            {
                Source(BackupRunSourceStatus.NewArchive, "history-new"),
                Source(BackupRunSourceStatus.Reused, "history-old"),
                Source(BackupRunSourceStatus.Unavailable)
            });

        Assert.IsNotNull(run);
        Assert.AreEqual(BackupRunStatus.Partial, run.Status);
        Assert.HasCount(3, run.Sources);
        Assert.IsTrue(BackupRunPolicy.IsHistoryItemReferenced("history-old", new[] { run }));
    }

    [TestMethod]
    public void KeepCountCountsRunsAndAlwaysKeepsImportantRuns()
    {
        var runs = Enumerable.Range(0, 5)
            .Select(index => new BackupRunRecord
            {
                RunId = $"run-{index}",
                ConfigId = "config",
                CompletedAtUtc = new DateTime(2026, 8, 7, index, 0, 0, DateTimeKind.Utc),
                IsImportant = index == 0
            })
            .ToList();

        var removed = BackupRunPolicy.SelectRunsToRemove(runs, keepCount: 2);

        CollectionAssert.AreEquivalent(new[] { "run-1", "run-2" }, removed.Select(run => run.RunId).ToArray());
        Assert.IsFalse(removed.Any(run => run.IsImportant));
    }

    private static BackupRunSourceRecord Source(
        BackupRunSourceStatus status,
        string historyItemId = "") => new()
    {
        FolderPath = "C:\\Game\\Saves",
        FolderName = "Saves",
        Status = status,
        HistoryItemId = historyItemId
    };
}
