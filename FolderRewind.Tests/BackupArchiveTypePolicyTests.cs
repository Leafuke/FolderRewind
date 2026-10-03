using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupArchiveTypePolicyTests
{
    [TestMethod]
    [DataRow("[Smart][2026-08-01]World.7z", "Smart")]
    [DataRow("prefix[SMART]suffix.zip", "Smart")]
    [DataRow("[Rolling][2026-08-01]World.7z", "Rolling")]
    [DataRow("[Overwrite][2026-08-01]World.7z", "Overwrite")]
    [DataRow("[Full][2026-08-01]World.7z", "Full")]
    [DataRow("backup.7z", "Full")]
    [DataRow("", "Full")]
    [DataRow(null, "Full")]
    public void FileNameInferencePreservesLegacyFallbacks(string? fileName, string expected)
    {
        Assert.AreEqual(expected, BackupArchiveTypePolicy.InferFromFileName(fileName));
    }

    [TestMethod]
    [DataRow("Incremental", true)]
    [DataRow("incremental", true)]
    [DataRow("Smart", true)]
    [DataRow("Full", false)]
    [DataRow("Overwrite", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IncrementalClassificationAcceptsHistoricalNames(string? backupType, bool expected)
    {
        Assert.AreEqual(expected, BackupArchiveTypePolicy.IsIncremental(backupType));
    }

    [TestMethod]
    [DataRow("Full", null, true)]
    [DataRow("Rolling", null, true)]
    [DataRow("Overwrite", null, true)]
    [DataRow("Smart", null, false)]
    [DataRow(null, "[Rolling][2026-08-01]World.7z", true)]
    public void SelfContainedClassificationIncludesRollingAndLegacyOverwrite(
        string? backupType,
        string? fileName,
        bool expected)
    {
        Assert.AreEqual(expected, BackupArchiveTypePolicy.IsSelfContained(backupType, fileName));
    }
}
