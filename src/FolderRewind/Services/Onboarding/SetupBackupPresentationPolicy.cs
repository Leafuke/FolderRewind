using FolderRewind.Models;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal enum SetupBackupSummaryState { Created, Complete, CompleteWithWarnings, Unchanged, Incomplete, Failed, Canceled, NeedsRecovery }

internal static class SetupBackupPresentationPolicy
{
    internal static SetupBackupSummaryState Summarize(bool attempted, IEnumerable<SetupBackupResult> results, int projectErrors)
    {
        if (!attempted) return SetupBackupSummaryState.Created;
        var snapshots = results.ToArray();
        var sources = snapshots.SelectMany(r => r.Sources).ToArray();
        if (snapshots.Any(r => r.RecoveryRequired) || sources.Any(s => s.Outcome == SetupSourceOutcome.NeedsRecovery))
            return SetupBackupSummaryState.NeedsRecovery;
        var successful = sources.Count(s => s.Outcome == SetupSourceOutcome.NoChange
            || s.Outcome == SetupSourceOutcome.Captured && s.VersionId is not null);
        if (projectErrors != 0 || successful != sources.Length || sources.Length == 0)
        {
            if (successful != 0) return SetupBackupSummaryState.Incomplete;
            return projectErrors == 0 && sources.Length != 0
                && sources.All(s => s.Outcome is SetupSourceOutcome.Canceled or SetupSourceOutcome.NotExecuted)
                ? SetupBackupSummaryState.Canceled : SetupBackupSummaryState.Failed;
        }
        if (snapshots.Any(r => r.HasWarnings)) return SetupBackupSummaryState.CompleteWithWarnings;
        return sources.All(s => s.Outcome == SetupSourceOutcome.NoChange)
            ? SetupBackupSummaryState.Unchanged : SetupBackupSummaryState.Complete;
    }
}
