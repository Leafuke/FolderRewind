using System.Collections.Generic;
using System.Linq;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services.Plugins.V3;

internal enum BackupSourceExecutionStatus
{
    NewArchive = 0,
    Reused = 1,
    Failed = 2,
    Unavailable = 3
}

internal sealed record PluginBackupRequestResult(
    OperationOutcome Outcome,
    bool CreatedNewArchive)
{
    public static PluginBackupRequestResult FromSource(
        BackupSourceExecutionStatus status,
        OperationOutcome? explicitOutcome = null,
        bool hasWarnings = false)
    {
        var outcome = explicitOutcome ?? status switch
        {
            BackupSourceExecutionStatus.NewArchive when hasWarnings => OperationOutcome.SuccessWithWarnings,
            BackupSourceExecutionStatus.NewArchive => OperationOutcome.Success,
            BackupSourceExecutionStatus.Reused => OperationOutcome.NoChanges,
            BackupSourceExecutionStatus.Failed or BackupSourceExecutionStatus.Unavailable => OperationOutcome.Failed,
            _ => OperationOutcome.Failed
        };
        return new PluginBackupRequestResult(outcome, status == BackupSourceExecutionStatus.NewArchive);
    }

    public static OperationOutcome Aggregate(IEnumerable<PluginBackupRequestResult> results)
    {
        var materialized = results.ToArray();
        if (materialized.Length == 0) return OperationOutcome.Blocked;
        return materialized
            .Select(value => value.Outcome)
            .OrderByDescending(Priority)
            .First();
    }

    private static int Priority(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Failed => 6,
        OperationOutcome.Blocked => 5,
        OperationOutcome.Canceled => 4,
        OperationOutcome.SuccessWithWarnings => 3,
        OperationOutcome.Success => 2,
        OperationOutcome.NoChanges => 1,
        _ => 0
    };
}
