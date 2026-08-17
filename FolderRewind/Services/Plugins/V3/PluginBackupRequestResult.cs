using System.Collections.Generic;
using System.Linq;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services.Plugins.V3;

internal sealed record PluginBackupRequestResult(
    OperationOutcome Outcome,
    bool CreatedNewArchive)
{
    public static PluginBackupRequestResult FromSource(
        BackupRunSourceStatus status,
        OperationOutcome? explicitOutcome = null,
        bool hasWarnings = false)
    {
        var outcome = explicitOutcome ?? status switch
        {
            BackupRunSourceStatus.NewArchive when hasWarnings => OperationOutcome.SuccessWithWarnings,
            BackupRunSourceStatus.NewArchive => OperationOutcome.Success,
            BackupRunSourceStatus.Reused => OperationOutcome.NoChanges,
            BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable => OperationOutcome.Failed,
            _ => OperationOutcome.Failed
        };
        return new PluginBackupRequestResult(outcome, status == BackupRunSourceStatus.NewArchive);
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
