using FolderRewind.Services;
using FolderRewind.History.Application;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MigrationHistoryStatusTests
{
    [TestMethod] public void PendingPresentationNeverMasksReadyReleasedMetadataOnlyOrConfirmedUnavailableHistory()
    {
        Assert.IsTrue(MigrationHistoryStatusPolicy.ShowPendingVersion(MigrationHistoryStatus.AwaitingRecovery, HistoryPresentationReadiness.Unavailable));
        foreach (var readiness in new[] { HistoryPresentationReadiness.Ready, HistoryPresentationReadiness.PayloadReleased, HistoryPresentationReadiness.MetadataOnly })
            Assert.IsFalse(MigrationHistoryStatusPolicy.ShowPendingVersion(MigrationHistoryStatus.AwaitingRecovery, readiness));
        foreach (var status in new[] { MigrationHistoryStatus.None, MigrationHistoryStatus.AwaitingPermissions, MigrationHistoryStatus.AwaitingHistory, MigrationHistoryStatus.MissingArchives })
            Assert.IsFalse(MigrationHistoryStatusPolicy.ShowPendingVersion(status, HistoryPresentationReadiness.Unavailable));
    }
}
