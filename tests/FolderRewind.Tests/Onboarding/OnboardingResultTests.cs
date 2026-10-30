using FolderRewind.Models;

namespace FolderRewind.Tests;

[TestClass]
public sealed class OnboardingResultTests
{
    [TestMethod]
    public void MixedSelectedSourcesPreserveVersionIdentityAndWarnings()
    {
        var result = new SetupBackupResult([
            new("a", "new", SetupSourceOutcome.Captured, "v1", false, []),
            new("b", "unchanged", SetupSourceOutcome.NoChange, "old", false, []),
            new("c", "partial", SetupSourceOutcome.Captured, "v2", true, ["consistency"]),
            new("d", "failed", SetupSourceOutcome.Failed, null, false, []),
            new("e", "canceled", SetupSourceOutcome.Canceled, null, false, [])
        ], "checkpoint", "run", false, DateTimeOffset.UtcNow);
        Assert.AreEqual(2, result.CreatedVersionCount);
        Assert.AreEqual(1, result.UnchangedCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(1, result.CanceledCount);
        Assert.IsTrue(result.HasWarnings);
        Assert.AreEqual("old", result.Sources[1].VersionId);
    }

    [TestMethod]
    public void UncommittedCaptureAndCarriedForwardIdentityDoNotCreateVersionCount()
    {
        var result = new SetupBackupResult([
            new("a", "uncommitted", SetupSourceOutcome.Captured, null, false, []),
            new("b", "reused", SetupSourceOutcome.NoChange, "existing", false, [])
        ], null, null, true, DateTimeOffset.UtcNow);
        Assert.AreEqual(0, result.CreatedVersionCount);
        Assert.AreEqual(1, result.UnchangedCount);
        Assert.IsTrue(result.HasWarnings);
    }

    [TestMethod]
    public void RetryReplacesOnlyItsSourceResultsAndRetainsRecoveryWarning()
    {
        var original = new SetupBackupResult([
            new("a", "completed", SetupSourceOutcome.Captured, "old-version", false, []),
            new("b", "retry", SetupSourceOutcome.Failed, null, false, [])
        ], "old-checkpoint", "old-run", true, DateTimeOffset.UnixEpoch);
        var retry = new SetupBackupResult([new("b", "retry", SetupSourceOutcome.Captured, "retry-version", false, [])],
            "retry-checkpoint", "retry-run", false, DateTimeOffset.UtcNow);
        var combined = original.MergeRetry(retry);
        Assert.HasCount(2, combined.Sources);
        Assert.AreEqual("old-version", combined.Sources.Single(s => s.SourceId == "a").VersionId);
        Assert.AreEqual("retry-version", combined.Sources.Single(s => s.SourceId == "b").VersionId);
        Assert.AreEqual("retry-run", combined.RunId);
        Assert.IsTrue(combined.RecoveryRequired);
    }
}
