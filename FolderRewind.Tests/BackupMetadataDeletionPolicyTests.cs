using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupMetadataDeletionPolicyTests
{
    [TestMethod]
    public void DeletingMiddleSmartRebasesSuccessorAgainstPreviousRecord()
    {
        var state = CreateState("smart-2.7z", "full.7z");
        var full = CreateRecord("full.7z", "Full", "full.7z", "", 1, ["a", "b"]);
        var deleted = CreateRecord("smart-1.7z", "Smart", "full.7z", "full.7z", 2, ["b", "c"]);
        deleted.AddedFiles = ["c"];
        deleted.ModifiedFiles = ["b"];
        deleted.DeletedFiles = ["a"];
        var successor = CreateRecord("smart-2.7z", "Smart", "full.7z", "smart-1.7z", 3, ["b", "c", "d"]);
        successor.AddedFiles = ["d"];
        successor.ModifiedFiles = ["c"];

        var result = BackupMetadataDeletionPolicy.Apply(
            state,
            [successor, full, deleted],
            "smart-1.7z",
            null,
            null,
            null);

        Assert.IsFalse(result.InvalidateState);
        Assert.HasCount(2, result.Records);
        var rebased = result.Records.Single(record => record.ArchiveFileName == "smart-2.7z");
        Assert.AreEqual("full.7z", rebased.PreviousBackupFileName);
        Assert.AreEqual("full.7z", rebased.BasedOnFullBackup);
        CollectionAssert.AreEqual(new[] { "c", "d" }, rebased.AddedFiles);
        CollectionAssert.AreEqual(new[] { "a" }, rebased.DeletedFiles);
        CollectionAssert.AreEqual(new[] { "b" }, rebased.ModifiedFiles);
    }

    [TestMethod]
    public void DeletingCurrentLastBackupInvalidatesState()
    {
        var state = CreateState("smart-1.7z", "full.7z");
        var full = CreateRecord("full.7z", "Full", "full.7z", "", 1, ["a"]);
        var smart = CreateRecord("smart-1.7z", "Smart", "full.7z", "full.7z", 2, ["a", "b"]);

        var result = BackupMetadataDeletionPolicy.Apply(
            state,
            [full, smart],
            "smart-1.7z",
            null,
            null,
            null);

        Assert.IsTrue(result.InvalidateState);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual("full.7z", result.Records[0].ArchiveFileName);
    }

    [TestMethod]
    public void ReplacingDeletedFullWithSuccessorFullKeepsStateValid()
    {
        var state = CreateState("smart-1.7z", "full-1.7z");
        var full = CreateRecord("full-1.7z", "Full", "full-1.7z", "", 1, ["a"]);
        var smart = CreateRecord("smart-1.7z", "Smart", "full-1.7z", "full-1.7z", 2, ["a", "b"]);

        var result = BackupMetadataDeletionPolicy.Apply(
            state,
            [full, smart],
            "full-1.7z",
            "smart-1.7z",
            "full-2.7z",
            "Full");

        Assert.IsFalse(result.InvalidateState);
        Assert.AreEqual("full-2.7z", result.State.LastBackupFileName);
        Assert.AreEqual("full-2.7z", result.State.BasedOnFullBackup);
        Assert.HasCount(1, result.Records);
        var replacement = result.Records[0];
        Assert.AreEqual("full-2.7z", replacement.ArchiveFileName);
        Assert.AreEqual("Full", replacement.BackupType);
        Assert.AreEqual("full-2.7z", replacement.BasedOnFullBackup);
        Assert.AreEqual(string.Empty, replacement.PreviousBackupFileName);
        CollectionAssert.AreEqual(new[] { "a", "b" }, replacement.AddedFiles);
    }

    [TestMethod]
    public void PolicyDoesNotMutateSourceObjects()
    {
        var state = CreateState("smart.7z", "full.7z");
        var full = CreateRecord("full.7z", "Full", "full.7z", "", 1, ["a"]);
        var smart = CreateRecord("smart.7z", "Smart", "full.7z", "full.7z", 2, ["a", "b"]);

        _ = BackupMetadataDeletionPolicy.Apply(state, [full, smart], "full.7z", "smart.7z", "replacement.7z", "Full");

        Assert.AreEqual("smart.7z", state.LastBackupFileName);
        Assert.AreEqual("smart.7z", smart.ArchiveFileName);
        Assert.AreEqual("Smart", smart.BackupType);
    }

    private static BackupMetadataState CreateState(string lastBackup, string basedOnFull)
    {
        return new BackupMetadataState
        {
            LastBackupFileName = lastBackup,
            BasedOnFullBackup = basedOnFull,
            FileStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static BackupChangeRecord CreateRecord(
        string fileName,
        string backupType,
        string basedOnFull,
        string previous,
        int minute,
        List<string> fullFileList)
    {
        return new BackupChangeRecord
        {
            ArchiveFileName = fileName,
            BackupType = backupType,
            BasedOnFullBackup = basedOnFull,
            PreviousBackupFileName = previous,
            CreatedAtUtc = new DateTime(2026, 8, 1, 0, minute, 0, DateTimeKind.Utc),
            FullFileList = fullFileList
        };
    }
}
