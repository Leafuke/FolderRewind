using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PluginBackupRequestResultTests
{
    [TestMethod]
    public void SourceResultsMapToThePublicOperationVocabulary()
    {
        Assert.AreEqual(
            OperationOutcome.NoChanges,
            PluginBackupRequestResult.FromSource(BackupRunSourceStatus.Reused).Outcome);
        Assert.AreEqual(
            OperationOutcome.Failed,
            PluginBackupRequestResult.FromSource(BackupRunSourceStatus.Failed).Outcome);
        Assert.AreEqual(
            OperationOutcome.Failed,
            PluginBackupRequestResult.FromSource(BackupRunSourceStatus.Unavailable).Outcome);
        Assert.AreEqual(
            OperationOutcome.Success,
            PluginBackupRequestResult.FromSource(BackupRunSourceStatus.NewArchive).Outcome);
        Assert.AreEqual(
            OperationOutcome.SuccessWithWarnings,
            PluginBackupRequestResult.FromSource(
                BackupRunSourceStatus.NewArchive,
                hasWarnings: true).Outcome);
        Assert.AreEqual(
            OperationOutcome.Blocked,
            PluginBackupRequestResult.FromSource(
                BackupRunSourceStatus.Failed,
                OperationOutcome.Blocked).Outcome);
        Assert.AreEqual(
            OperationOutcome.Canceled,
            PluginBackupRequestResult.FromSource(
                BackupRunSourceStatus.Failed,
                OperationOutcome.Canceled).Outcome);
    }

    [TestMethod]
    public void MixedFolderAggregationUsesTheFrozenFailurePrecedence()
    {
        var descending = new[]
        {
            OperationOutcome.NoChanges,
            OperationOutcome.Success,
            OperationOutcome.SuccessWithWarnings,
            OperationOutcome.Canceled,
            OperationOutcome.Blocked,
            OperationOutcome.Failed
        };

        for (var index = 0; index < descending.Length; index++)
        {
            var results = descending.Take(index + 1)
                .Select(outcome => new PluginBackupRequestResult(outcome, outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings))
                .ToArray();
            Assert.AreEqual(descending[index], PluginBackupRequestResult.Aggregate(results));
        }
        Assert.AreEqual(OperationOutcome.Blocked, PluginBackupRequestResult.Aggregate([]));
    }
}
