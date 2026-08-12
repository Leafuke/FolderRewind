using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PluginV2SemanticBaselineTests
{
    [TestMethod]
    [DataRow(BackupInvocationSource.Manual, true)]
    [DataRow(BackupInvocationSource.Automatic, true)]
    [DataRow(BackupInvocationSource.Remote, true)]
    [DataRow(BackupInvocationSource.PluginHotkey, true)]
    [DataRow(BackupInvocationSource.Internal, false)]
    public void BackupEntryPointsPreserveConsistencyIntent(
        BackupInvocationSource expectedSource,
        bool expectedConsistency)
    {
        var options = expectedSource switch
        {
            BackupInvocationSource.Manual => BackupInvocationOptions.ForManual(),
            BackupInvocationSource.Automatic => BackupInvocationOptions.ForAutomatic(),
            BackupInvocationSource.Remote => BackupInvocationOptions.ForRemote(),
            BackupInvocationSource.PluginHotkey => BackupInvocationOptions.ForPluginHotkey(),
            BackupInvocationSource.Internal => BackupInvocationOptions.ForInternal(),
            _ => throw new ArgumentOutOfRangeException(nameof(expectedSource))
        };

        Assert.AreEqual(expectedSource, options.Source);
        Assert.AreEqual(expectedConsistency, options.PreferApplicationConsistentSnapshot);
    }

    [TestMethod]
    public async Task RestoreMutationDelegateIsNeverCalledForFailedOrUnavailableSources()
    {
        var run = new BackupRunRecord
        {
            RunId = "run",
            ConfigId = "config",
            Sources =
            {
                new BackupRunSourceRecord
                {
                    FolderPath = "failed",
                    Status = BackupRunSourceStatus.Failed,
                    HistoryItemId = "history-failed"
                },
                new BackupRunSourceRecord
                {
                    FolderPath = "unavailable",
                    Status = BackupRunSourceStatus.Unavailable,
                    HistoryItemId = "history-unavailable"
                },
                new BackupRunSourceRecord
                {
                    FolderPath = "missing-reference",
                    Status = BackupRunSourceStatus.NewArchive,
                    HistoryItemId = ""
                }
            }
        };
        var mutationCalls = 0;

        var result = await BackupRunRestoreOrchestrator.RestoreAsync(run, source =>
        {
            mutationCalls++;
            return Task.FromResult(new BackupRunRestoreSourceResult
            {
                FolderPath = source.FolderPath,
                Success = true
            });
        });

        Assert.AreEqual(0, mutationCalls);
        Assert.HasCount(3, result.Sources);
        Assert.IsTrue(result.Sources.All(source => !source.Success));
    }

    [TestMethod]
    public async Task RestoreMutationDelegateRunsOnceForEachRecoverableSource()
    {
        var run = new BackupRunRecord
        {
            RunId = "run",
            ConfigId = "config",
            Sources =
            {
                new BackupRunSourceRecord
                {
                    FolderPath = "new",
                    Status = BackupRunSourceStatus.NewArchive,
                    HistoryItemId = "history-new"
                },
                new BackupRunSourceRecord
                {
                    FolderPath = "reused",
                    Status = BackupRunSourceStatus.Reused,
                    HistoryItemId = "history-reused"
                }
            }
        };
        var calls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = await BackupRunRestoreOrchestrator.RestoreAsync(run, source =>
        {
            calls[source.FolderPath] = calls.GetValueOrDefault(source.FolderPath) + 1;
            return Task.FromResult(new BackupRunRestoreSourceResult
            {
                FolderPath = source.FolderPath,
                Success = true
            });
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, calls["new"]);
        Assert.AreEqual(1, calls["reused"]);
    }
}
